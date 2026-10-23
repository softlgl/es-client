using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EsClient.Api.Es;

namespace EsClient.Api.Services;

/// <summary>连接条目：保存建连凭据（用于断开后重连）+ 活动客户端 + 最近一次集群信息。</summary>
public class ConnectionEntry
{
    public required ConnectRequest Request { get; set; }
    public EsHttpClient? Client { get; set; }
    public Es.ConnectionInfo Info { get; set; } = null!;
    public bool Connected => Client != null;
}

/// <summary>
/// 多连接管理：以连接 URL 为 ID 保存多个集群连接，维护一个活动连接（本地单人工具）。
/// 连接列表（含认证凭据）持久化到 App_Data/connections.json，重启后可重连。
/// </summary>
public class ConnectionStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 既有 connections.json 用的是小写键（此前由手写匿名对象的属性名决定）。
        // 换成强类型 DTO 后必须显式声明，否则属性名会变成 PascalCase 导致旧配置读不回来。
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, ConnectionEntry> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ConnectionStore> _logger;
    private string? _activeId;

    public ConnectionStore(IHostEnvironment env, ILogger<ConnectionStore> logger)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "connections.json");
        _logger = logger;
        Load();
    }

    public bool IsConnected => Active != null;

    public EsHttpClient? Active
    {
        get
        {
            lock (_lock)
                return _activeId != null &&
                       _map.TryGetValue(_activeId, out var e) ? e.Client : null;
        }
    }

    /// <summary>添加或替换连接（同 URL 视为同一连接），并将其设为活动连接。</summary>
    public void Set(EsHttpClient client, ConnectRequest request)
    {
        lock (_lock)
        {
            var id = client.Info.Url;
            if (_map.TryGetValue(id, out var old))
            {
                old.Client?.Dispose();
                old.Client = client;
                old.Info = client.Info;
                old.Request = request; // 认证信息可能已变更
            }
            else
            {
                _map[id] = new ConnectionEntry { Request = request, Client = client, Info = client.Info };
            }
            _activeId = id;
            Save();
        }
    }

    /// <summary>切换活动连接（目标可能处于断开状态，由调用方负责重连）。</summary>
    public ConnectionEntry? Switch(string id)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var e))
                throw new EsException($"连接不存在：{id}", 404);
            _activeId = id;
            return e;
        }
    }

    /// <summary>断开当前活动连接：释放客户端但保留条目（凭据+集群信息），可随时重连。</summary>
    public void Deactivate()
    {
        lock (_lock)
        {
            if (_activeId != null && _map.TryGetValue(_activeId, out var e) && e.Client != null)
            {
                e.Client.Dispose();
                e.Client = null;
            }
            _activeId = null;
        }
    }

    /// <summary>删除指定连接；若删除的是活动连接则活动置空。</summary>
    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_map.Remove(id, out var e))
            {
                e.Client?.Dispose();
                if (_activeId == id) _activeId = null;
                Save();
            }
        }
    }

    /// <summary>原位更新连接配置（地址不变，仅名称/凭据变化），不动连接状态。</summary>
    public void UpdateRequest(string id, ConnectRequest req)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(id, out var e))
                throw new EsException($"连接不存在：{id}", 404);
            e.Request = req;
            if (e.Client != null) e.Info = e.Client.Info; // 名称随最新信息刷新
            Save();
        }
    }

    /// <summary>保存连接配置但不建连（编辑非活动连接的地址时用），不改活动连接。</summary>
    public void SaveInactive(ConnectRequest req, Es.ConnectionInfo info)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(req.Url, out var old))
            {
                old.Client?.Dispose();
                old.Client = null;
                old.Request = req;
                old.Info = info;
            }
            else
            {
                _map[req.Url] = new ConnectionEntry { Request = req, Client = null, Info = info };
            }
            Save();
        }
    }

    public ConnectionEntry? GetEntry(string id)
    {
        lock (_lock) return _map.TryGetValue(id, out var e) ? e : null;
    }

    public List<ConnectionEntry> List()
    {
        lock (_lock) return _map.Values.ToList();
    }

    /// <summary>宿主关闭时释放全部活动客户端（Singleton 由 DI 自动调用 Dispose）。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var e in _map.Values)
                e.Client?.Dispose();
            _map.Clear();
            _activeId = null;
        }
    }

    /// <summary>取当前客户端，未连接时抛 EsException。</summary>
    public EsHttpClient Require()
    {
        var c = Active;
        if (c == null)
            throw new EsException("尚未连接到 Elasticsearch，请先建立连接", 409);
        return c;
    }

    /// <summary>
    /// 取当前活动连接的建连请求（含凭据）。导出等长任务据此新建独立连接执行，
    /// 而非直接引用活动客户端 —— 否则主连接被断开/重连 Dispose 时会波及进行中的任务。
    /// </summary>
    public ConnectRequest RequireActiveRequest()
    {
        lock (_lock)
        {
            var e = _activeId != null && _map.TryGetValue(_activeId, out var entry) ? entry : null;
            if (e?.Client == null)
                throw new EsException("尚未连接到 Elasticsearch，请先建立连接", 409);
            return e.Request;
        }
    }

    // ---------- 持久化 ----------
    /// <summary>
    /// connections.json 的持久化结构。
    /// 此前「存」用手写匿名对象、「读」用逐字段 JsonNode 取值，加一个字段要改两处，
    /// 且键名拼错不会编译报错、只会静默丢值。改为强类型后由序列化器保证两侧一致。
    /// </summary>
    private sealed class PersistedFile
    {
        public List<PersistedConnection> Connections { get; set; } = [];
    }

    private sealed class PersistedConnection
    {
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? ApiKey { get; set; }
        public bool Insecure { get; set; }
        public string? ClusterName { get; set; }
        public string? Version { get; set; }
        public int Major { get; set; }
    }

    private void Save()
    {
        try
        {
            var doc = new PersistedFile
            {
                Connections = _map.Values.Select(e => new PersistedConnection
                {
                    Name = e.Request.Name,
                    Url = e.Request.Url,
                    Username = e.Request.Username,
                    Password = e.Request.Password,
                    ApiKey = e.Request.ApiKey,
                    Insecure = e.Request.Insecure,
                    ClusterName = e.Info.ClusterName,
                    Version = e.Info.Version,
                    Major = e.Info.Major
                }).ToList()
            };
            AtomicFile.Write(_filePath, JsonSerializer.Serialize(doc, JsonOpts));
        }
        // 落盘失败不影响本次已成功的连接（内存态仍可用），但必须留痕——
        // 此前是空的 catch{}，用户的连接配置静默丢失却毫无线索
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接配置落盘失败（内存态不受影响）：{Path}", _filePath);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var file = JsonSerializer.Deserialize<PersistedFile>(File.ReadAllText(_filePath), JsonOpts);
            if (file?.Connections == null) return;
            foreach (var c in file.Connections)
            {
                if (string.IsNullOrEmpty(c.Url)) continue;
                var request = new ConnectRequest(
                    c.Url,
                    c.Username,
                    c.Password,
                    c.ApiKey,
                    c.Insecure,
                    c.Name);
                var info = new Es.ConnectionInfo(
                    c.Url,
                    c.ClusterName ?? "unknown",
                    c.Version ?? "unknown",
                    c.Major);
                _map[c.Url] = new ConnectionEntry { Request = request, Info = info, Client = null };
            }
        }
        catch (Exception ex)
        {
            // 文件损坏/不可读则当作无历史，但留痕以便排查（不静默）
            _logger.LogWarning(ex, "连接配置读取失败，按空列表处理：{Path}", _filePath);
        }
    }
}
