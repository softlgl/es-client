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
public class ConnectionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private readonly Dictionary<string, ConnectionEntry> _map = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeId;

    public ConnectionStore(IHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "connections.json");
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

    /// <summary>取当前客户端，未连接时抛 EsException。</summary>
    public EsHttpClient Require()
    {
        var c = Active;
        if (c == null)
            throw new EsException("尚未连接到 Elasticsearch，请先建立连接", 409);
        return c;
    }

    // ---------- 持久化 ----------

    private void Save()
    {
        try
        {
            var doc = new
            {
                connections = _map.Values.Select(e => new
                {
                    name = e.Request.Name,
                    url = e.Request.Url,
                    username = e.Request.Username,
                    password = e.Request.Password,
                    apiKey = e.Request.ApiKey,
                    insecure = e.Request.Insecure,
                    clusterName = e.Info.ClusterName,
                    version = e.Info.Version,
                    major = e.Info.Major
                })
            };
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, JsonOpts));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch { /* 保存失败不阻塞连接操作 */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var node = JsonNode.Parse(File.ReadAllText(_filePath));
            if (node?["connections"] is not JsonArray arr) return;
            foreach (var n in arr.OfType<JsonObject>())
            {
                var url = n["url"]?.GetValue<string>();
                if (string.IsNullOrEmpty(url)) continue;
                var request = new ConnectRequest(
                    url,
                    n["username"]?.GetValue<string>(),
                    n["password"]?.GetValue<string>(),
                    n["apiKey"]?.GetValue<string>(),
                    n["insecure"]?.GetValue<bool>() ?? false,
                    n["name"]?.GetValue<string>());
                var info = new Es.ConnectionInfo(
                    url,
                    n["clusterName"]?.GetValue<string>() ?? "unknown",
                    n["version"]?.GetValue<string>() ?? "unknown",
                    n["major"]?.GetValue<int>() ?? 0);
                _map[url] = new ConnectionEntry { Request = request, Info = info, Client = null };
            }
        }
        catch { /* 文件损坏则忽略，当作无保存 */ }
    }
}
