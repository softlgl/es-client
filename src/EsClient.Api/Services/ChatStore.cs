using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EsClient.Api.Services;

/// <summary>
/// AI 问答会话持久化：App_Data/chats/{index}.json，每个索引一个文件、可含多个会话。
/// 单会话轮数与索引会话数均有上限，防止文件无限膨胀。
/// </summary>
public class ChatStore
{
    public record ChatTurn(string Role, string Text, string? Dsl, JsonNode? Data, DateTimeOffset Time);
    public class ChatSession
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "新会话";
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<ChatTurn> Turns { get; set; } = [];
    }
    private record ChatFile(List<ChatSession> Sessions);

    private const int MaxSessions = 20;   // 每索引最多保留会话数，超出清最久未更新的
    private const int MaxTurns = 50;      // 每会话最多保留轮数（一问一答算两 turn），超出裁最旧的

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private readonly string _dir;
    private readonly ILogger<ChatStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChatStore(IWebHostEnvironment env, ILogger<ChatStore> logger)
    {
        _dir = Path.Combine(env.ContentRootPath, "App_Data", "chats");
        _logger = logger;
    }

    /// <summary>索引名 → 文件路径（替换非法文件名字符）。</summary>
    private string PathFor(string index)
    {
        var safe = System.Text.RegularExpressions.Regex.Replace(index, "[^A-Za-z0-9_.-]", "_");
        return Path.Combine(_dir, safe + ".json");
    }

    /// <summary>列出索引下的全部会话（按更新时间倒序，不含轮次内容）。</summary>
    public async Task<List<ChatSession>> ListAsync(string index)
    {
        var file = await LoadAsync(index);
        return file.Sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public async Task<ChatSession?> GetAsync(string index, string sessionId)
    {
        var file = await LoadAsync(index);
        return file.Sessions.FirstOrDefault(s => s.Id == sessionId);
    }

    /// <summary>追加轮次；会话不存在则创建（标题取第一句提问）。超出上限时裁剪。</summary>
    public async Task<ChatSession> AddTurnsAsync(string index, string sessionId, IReadOnlyList<ChatTurn> turns)
    {
        await _gate.WaitAsync();
        try
        {
            var file = await LoadAsync(index);
            var session = file.Sessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null)
            {
                session = new ChatSession { Id = sessionId, Title = "新会话", CreatedAt = DateTimeOffset.Now, UpdatedAt = DateTimeOffset.Now, Turns = [] };
                file.Sessions.Add(session);
                if (file.Sessions.Count > MaxSessions)
                {
                    // 按 UpdatedAt 淘汰最旧的超额会话（新会话不在淘汰之列）
                    var victims = file.Sessions
                        .Where(s => s != session)
                        .OrderBy(s => s.UpdatedAt)
                        .Take(file.Sessions.Count - MaxSessions)
                        .Select(s => s.Id).ToHashSet();
                    file.Sessions.RemoveAll(s => victims.Contains(s.Id));
                    _logger.LogInformation("AI 问答会话超出上限，已清理索引 {Index} 的 {Count} 个旧会话", index, victims.Count);
                }
            }

            session.Turns.AddRange(turns);
            if (session.Turns.Count > MaxTurns)
                session.Turns.RemoveRange(0, session.Turns.Count - MaxTurns);

            var firstUser = session.Turns.FirstOrDefault(t => t.Role == "user")?.Text;
            if (!string.IsNullOrWhiteSpace(firstUser))
                session.Title = firstUser.Length <= 30 ? firstUser : firstUser[..30] + "…";
            session.UpdatedAt = DateTimeOffset.Now;

            await SaveAsync(index, file);
            return session;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string index, string sessionId)
    {
        await _gate.WaitAsync();
        try
        {
            var file = await LoadAsync(index);
            file.Sessions.RemoveAll(s => s.Id == sessionId);
            await SaveAsync(index, file);
        }
        finally { _gate.Release(); }
    }

    private async Task<ChatFile> LoadAsync(string index)
    {
        var path = PathFor(index);
        if (!File.Exists(path)) return new ChatFile([]);
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var file = JsonSerializer.Deserialize<ChatFile>(json, JsonOpts);
            return file ?? new ChatFile([]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 问答历史读取失败，按空会话处理：{Path}", path);
            return new ChatFile([]);
        }
    }

    private async Task SaveAsync(string index, ChatFile file)
    {
        Directory.CreateDirectory(_dir);
        var path = PathFor(index);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(file, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }
}
