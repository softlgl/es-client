using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace EsClient.Api.Services;

/// <summary>
/// AI 问答会话持久化：App_Data/chat.db（SQLite）。
/// 会话按索引归档、轮次逐条追加（不再每次整文件重写）；单会话轮数与索引会话数仍有上限，写入时裁剪最旧数据。
/// 首次启动会把旧的 App_Data/chats/*.json 迁移入库，并将原文件改名为 *.imported 归档。
/// </summary>
public class ChatStore
{
    // Steps：本轮工具探索的结构化留痕（[{name,display,args,result,ok,ms}]），仅带工具且真调用时非空；
    // Reasoning：生成阶段模型思考过程（思考模式）全文，落库后前端以折叠区重放。
    public record ChatTurn(string Role, string Text, string? Dsl, JsonNode? Data, DateTimeOffset Time,
        JsonNode? Steps = null, string? Reasoning = null);

    public class ChatSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "新会话";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<ChatTurn> Turns { get; set; } = [];
    }

    /// <summary>会话列表项（不含轮次内容，仅带轮数）。</summary>
    public record ChatSessionSummary(string Id, string Title, DateTimeOffset UpdatedAt, int TurnCount);

    private const int MaxSessions = 200;  // 每索引最多保留会话数，超出清最久未更新的
    private const int MaxTurns = 200;     // 每会话最多保留轮数（一问一答算两 turn），超出裁最旧的

    /// <summary>旧 JSON 会话文件的读取选项（camelCase，与落盘格式一致）。</summary>
    private static readonly JsonSerializerOptions LegacyJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dbPath;
    private readonly string _legacyDir;
    private readonly ILogger<ChatStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);   // 写串行，避免并发写库冲突
    private readonly object _initLock = new();
    private Task? _initTask;

    public ChatStore(IWebHostEnvironment env, ILogger<ChatStore> logger)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "App_Data");
        _dbPath = Path.Combine(dataDir, "chat.db");
        _legacyDir = Path.Combine(dataDir, "chats");
        _logger = logger;
    }

    /// <summary>列出索引下的全部会话（按更新时间倒序，不含轮次内容）。</summary>
    public async Task<List<ChatSessionSummary>> ListAsync(string index)
    {
        await EnsureInitializedAsync();
        await using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_key, s.title, s.updated_at,
                   (SELECT COUNT(*) FROM turns t WHERE t.sid = s.sid)
            FROM sessions s
            WHERE s.index_name = @index
            ORDER BY julianday(s.updated_at) DESC;
            """;
        cmd.Parameters.AddWithValue("@index", index);

        var list = new List<ChatSessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add(new ChatSessionSummary(reader.GetString(0), reader.GetString(1),
                ParseTime(reader.GetString(2)), reader.GetInt32(3)));
        return list;
    }

    public async Task<ChatSession?> GetAsync(string index, string sessionId)
    {
        await EnsureInitializedAsync();
        await using var conn = Open();
        return await LoadSessionAsync(conn, null, index, sessionId);
    }

    /// <summary>追加轮次；会话不存在则创建（标题取第一句提问）。超出上限时裁剪。</summary>
    public async Task<ChatSession> AddTurnsAsync(string index, string sessionId, IReadOnlyList<ChatTurn> turns)
    {
        await EnsureInitializedAsync();
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

            var now = DateTimeOffset.Now;
            var sid = FindSessionSid(conn, tx, index, sessionId);
            if (sid is null)
            {
                sid = InsertSession(conn, tx, index, sessionId, "新会话", now, now);
                TrimExcessSessions(conn, tx, index, sid.Value);
            }

            foreach (var turn in turns)
                InsertTurn(conn, tx, sid.Value, turn);

            TrimExcessTurns(conn, tx, sid.Value);

            // 标题取（裁剪后）会话内第一句提问，与旧 JSON 实现保持一致
            var firstUser = FirstUserText(conn, tx, sid.Value);
            var title = string.IsNullOrWhiteSpace(firstUser)
                ? "新会话"
                : firstUser.Length <= 30 ? firstUser : firstUser[..30] + "…";
            UpdateSessionMeta(conn, tx, sid.Value, title, now);

            var session = (await LoadSessionAsync(conn, tx, index, sessionId))!;
            await tx.CommitAsync();
            return session;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string index, string sessionId)
    {
        await EnsureInitializedAsync();
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
            var sid = FindSessionSid(conn, tx, index, sessionId);
            if (sid is null) return;

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM turns WHERE sid = @sid; DELETE FROM sessions WHERE sid = @sid;";
            cmd.Parameters.AddWithValue("@sid", sid.Value);
            cmd.ExecuteNonQuery();

            await tx.CommitAsync();
        }
        finally { _gate.Release(); }
    }

    // ---------- 连接与初始化 ----------

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout = 5000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private Task EnsureInitializedAsync()
    {
        if (_initTask is not null) return _initTask;
        lock (_initLock)
            return _initTask ??= InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            // WAL：读写并发不互相阻塞；会话/轮次按索引、会话建索引
            cmd.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS sessions (
                    sid         INTEGER PRIMARY KEY AUTOINCREMENT,
                    index_name  TEXT NOT NULL,
                    session_key TEXT NOT NULL,
                    title       TEXT NOT NULL,
                    created_at  TEXT NOT NULL,
                    updated_at  TEXT NOT NULL,
                    UNIQUE(index_name, session_key)
                );
                CREATE INDEX IF NOT EXISTS ix_sessions_index ON sessions(index_name, updated_at);
                CREATE TABLE IF NOT EXISTS turns (
                    tid       INTEGER PRIMARY KEY AUTOINCREMENT,
                    sid       INTEGER NOT NULL,
                    role      TEXT NOT NULL,
                    text      TEXT NOT NULL,
                    dsl       TEXT,
                    data      TEXT,
                    steps     TEXT,
                    reasoning TEXT,
                    time      TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_turns_sid ON turns(sid, tid);
                """;
            cmd.ExecuteNonQuery();
        }

        await MigrateLegacyAsync(conn);
    }

    /// <summary>把旧的 App_Data/chats/{index}.json 一次性导入；成功后原文件改名归档，失败则保留待下次重试。</summary>
    private async Task MigrateLegacyAsync(SqliteConnection conn)
    {
        if (!Directory.Exists(_legacyDir)) return;
        foreach (var file in Directory.EnumerateFiles(_legacyDir, "*.json"))
        {
            try
            {
                var index = Path.GetFileNameWithoutExtension(file);
                var json = await File.ReadAllTextAsync(file);
                var legacy = JsonSerializer.Deserialize<LegacyChatFile>(json, LegacyJsonOpts);

                var imported = 0;
                if (legacy?.Sessions is { Count: > 0 })
                {
                    await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
                    foreach (var s in legacy.Sessions)
                    {
                        if (string.IsNullOrWhiteSpace(s.Id)) continue;
                        // 已导入过的会话跳过，避免中断后重跑产生重复轮次
                        if (FindSessionSid(conn, tx, index, s.Id) is not null) continue;

                        var created = s.CreatedAt == default ? DateTimeOffset.Now : s.CreatedAt;
                        var updated = s.UpdatedAt == default ? created : s.UpdatedAt;
                        var sid = InsertSession(conn, tx, index, s.Id, s.Title ?? "新会话", created, updated);
                        foreach (var t in s.Turns)
                            InsertTurn(conn, tx, sid, t);
                        imported++;
                    }
                    await tx.CommitAsync();
                }

                Archive(file);
                _logger.LogInformation("AI 问答历史已迁移：{File}，{Count} 个会话", Path.GetFileName(file), imported);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI 问答历史迁移失败，跳过并保留原文件：{File}", file);
            }
        }
    }

    private void Archive(string file)
    {
        try
        {
            File.Move(file, file + ".imported", overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "旧会话文件归档失败：{File}", file);
        }
    }

    // ---------- SQL 辅助 ----------

    private static long? FindSessionSid(SqliteConnection conn, SqliteTransaction? tx, string index, string sessionKey)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sid FROM sessions WHERE index_name = @index AND session_key = @key;";
        cmd.Parameters.AddWithValue("@index", index);
        cmd.Parameters.AddWithValue("@key", sessionKey);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? null : Convert.ToInt64(v);
    }

    private static long InsertSession(SqliteConnection conn, SqliteTransaction? tx,
        string index, string sessionKey, string title, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO sessions(index_name, session_key, title, created_at, updated_at)
            VALUES(@index, @key, @title, @created, @updated);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@index", index);
        cmd.Parameters.AddWithValue("@key", sessionKey);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@created", FormatTime(createdAt));
        cmd.Parameters.AddWithValue("@updated", FormatTime(updatedAt));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void InsertTurn(SqliteConnection conn, SqliteTransaction? tx, long sid, ChatTurn turn)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO turns(sid, role, text, dsl, data, steps, reasoning, time)
            VALUES(@sid, @role, @text, @dsl, @data, @steps, @reasoning, @time);
            """;
        cmd.Parameters.AddWithValue("@sid", sid);
        cmd.Parameters.AddWithValue("@role", turn.Role);
        cmd.Parameters.AddWithValue("@text", turn.Text ?? "");
        cmd.Parameters.AddWithValue("@dsl", (object?)turn.Dsl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@data", (object?)turn.Data?.ToJsonString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@steps", (object?)turn.Steps?.ToJsonString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@reasoning", (object?)turn.Reasoning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@time", FormatTime(turn.Time));
        cmd.ExecuteNonQuery();
    }

    private static string? FirstUserText(SqliteConnection conn, SqliteTransaction? tx, long sid)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT text FROM turns WHERE sid = @sid AND role = 'user' ORDER BY tid LIMIT 1;";
        cmd.Parameters.AddWithValue("@sid", sid);
        return cmd.ExecuteScalar() as string;
    }

    private static void UpdateSessionMeta(SqliteConnection conn, SqliteTransaction? tx,
        long sid, string title, DateTimeOffset updatedAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE sessions SET title = @title, updated_at = @updated WHERE sid = @sid;";
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@updated", FormatTime(updatedAt));
        cmd.Parameters.AddWithValue("@sid", sid);
        cmd.ExecuteNonQuery();
    }

    /// <summary>会话数超上限时淘汰最久未更新的（新建的那个永不淘汰）。</summary>
    private static void TrimExcessSessions(SqliteConnection conn, SqliteTransaction tx, string index, long keepSid)
    {
        using var countCmd = conn.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*) FROM sessions WHERE index_name = @index;";
        countCmd.Parameters.AddWithValue("@index", index);
        var extra = Convert.ToInt32(countCmd.ExecuteScalar()) - MaxSessions;
        if (extra <= 0) return;

        // 先删轮次再删会话；两处子查询排序一致，取到的是同一批 sid
        foreach (var table in new[] { "turns", "sessions" })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                DELETE FROM {table} WHERE sid IN (
                    SELECT sid FROM sessions
                    WHERE index_name = @index AND sid <> @keep
                    ORDER BY julianday(updated_at) ASC
                    LIMIT @extra
                );
                """;
            cmd.Parameters.AddWithValue("@index", index);
            cmd.Parameters.AddWithValue("@keep", keepSid);
            cmd.Parameters.AddWithValue("@extra", extra);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>轮次超上限时裁掉最旧的，只保留最近 MaxTurns 条。</summary>
    private static void TrimExcessTurns(SqliteConnection conn, SqliteTransaction tx, long sid)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM turns
            WHERE sid = @sid AND tid NOT IN (
                SELECT tid FROM turns WHERE sid = @sid ORDER BY tid DESC LIMIT @max
            );
            """;
        cmd.Parameters.AddWithValue("@sid", sid);
        cmd.Parameters.AddWithValue("@max", MaxTurns);
        cmd.ExecuteNonQuery();
    }

    private static async Task<ChatSession?> LoadSessionAsync(SqliteConnection conn, SqliteTransaction? tx,
        string index, string sessionKey)
    {
        ChatSession session;
        long sid;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT sid, title, created_at, updated_at
                FROM sessions WHERE index_name = @index AND session_key = @key;
                """;
            cmd.Parameters.AddWithValue("@index", index);
            cmd.Parameters.AddWithValue("@key", sessionKey);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            sid = reader.GetInt64(0);
            session = new ChatSession
            {
                Id = sessionKey,
                Title = reader.GetString(1),
                CreatedAt = ParseTime(reader.GetString(2)),
                UpdatedAt = ParseTime(reader.GetString(3))
            };
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT role, text, dsl, data, steps, reasoning, time
                FROM turns WHERE sid = @sid ORDER BY tid;
                """;
            cmd.Parameters.AddWithValue("@sid", sid);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                session.Turns.Add(new ChatTurn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3)),
                    ParseTime(reader.GetString(6)),
                    reader.IsDBNull(4) ? null : JsonNode.Parse(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return session;
    }

    private static string FormatTime(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v)
            ? v : DateTimeOffset.Now;

    /// <summary>旧版 JSON 文件结构：{ "sessions": [ { id, title, createdAt, updatedAt, turns: [...] } ] }。</summary>
    private record LegacyChatFile(List<ChatSession> Sessions);
}
