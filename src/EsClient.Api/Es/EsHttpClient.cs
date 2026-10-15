using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace EsClient.Api.Es;

/// <summary>建连请求。</summary>
public record ConnectRequest(string Url, string? Username, string? Password, string? ApiKey, bool Insecure, string? Name = null);

/// <summary>
/// 基于 HttpClient 的 ES 客户端实现。请求 DSL 全部为原生 JSON，
/// 保证 7.x / 8.x 行为一致；错误体统一解析为 EsException。
/// </summary>
public class EsHttpClient : IEsClient
{
    private readonly HttpClient _http;

    public ConnectionInfo Info { get; }

    private EsHttpClient(HttpClient http, ConnectionInfo info)
    {
        _http = http;
        Info = info;
    }

    /// <summary>探测版本并构建客户端；连接失败抛出带原因的 EsException。</summary>
    public static async Task<EsHttpClient> CreateAsync(ConnectRequest req, CancellationToken ct = default)
    {
        var url = req.Url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new EsException("ES 地址无效，需为 http:// 或 https:// 开头的完整地址", 400);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        if (req.Insecure)
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.BaseAddress = new Uri(url + "/");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        if (!string.IsNullOrEmpty(req.ApiKey))
        {
            var key = req.ApiKey.Trim();
            // 用户可能粘贴已 base64 的 key（不含冒号）或原始 id:secret
            if (key.Contains(':') && !key.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
                key = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("ApiKey", key.StartsWith("ApiKey ") ? key[7..] : key);
        }
        else if (!string.IsNullOrEmpty(req.Username))
        {
            var pwd = req.Password ?? "";
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{req.Username}:{pwd}")));
        }

        try
        {
            var probe = new EsHttpClient(http, new ConnectionInfo(url, "", "", 0));
            var root = await probe.GetRootAsync(ct);
            var version = root["version"]?["number"]?.GetValue<string>() ?? "unknown";
            var majorStr = version.Split('.')[0];
            if (!int.TryParse(majorStr, out var major))
                throw new EsException($"无法解析 ES 版本号：{version}", 400);
            if (major < 7)
                throw new EsException($"不支持的 ES 版本 {version}（仅支持 7.x / 8.x / 9.x）", 400);
            var info = new ConnectionInfo(
                url,
                root["cluster_name"]?.GetValue<string>() ?? "unknown",
                version, major);
            return new EsHttpClient(http, info);
        }
        catch (EsException)
        {
            http.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            http.Dispose();
            throw new EsException(
                $"无法连接到 {url}：{ex.Message}（请确认地址、网络与认证信息）", 502);
        }
    }

    // ---------- 底层请求 ----------

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path,
        string? jsonBody = null, string? contentType = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, path);
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody,
                Encoding.UTF8, contentType ?? "application/json");
        using var res = await _http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            string? esType = null, reason = null, rootType = null;
            try
            {
                var node = JsonNode.Parse(text);
                var err = node?["error"];
                if (err is JsonObject errObj)
                {
                    esType = errObj["type"]?.GetValue<string>();
                    reason = errObj["reason"]?.GetValue<string>();
                    if (errObj["root_cause"] is JsonArray rc && rc.Count > 0)
                    {
                        rootType = rc[0]?["type"]?.GetValue<string>();
                        reason ??= rc[0]?["reason"]?.GetValue<string>();
                    }
                }
                else if (err != null)
                {
                    reason = err.ToJsonString();
                }
            }
            catch { /* 错误体非 JSON，走通用文案 */ }

            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new EsException("认证失败（401），请检查用户名/密码/API Key", 401, esType);
            throw EsException.FromResponse((int)res.StatusCode, esType, reason, rootType);
        }

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static string Serialize(JsonNode? node) => node?.ToJsonString() ?? "";

    // ---------- IEsClient ----------

    public async Task<JsonObject> GetRootAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, "", ct: ct);
        return res as JsonObject ?? throw new EsException("ES 响应格式异常（缺少根对象）");
    }

    public async Task<JsonArray> CatIndicesAsync(CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get,
            "_cat/indices?format=json&bytes=b&h=health,status,index,docs.count,store.size", ct: ct);
        return res as JsonArray ?? new JsonArray();
    }

    public async Task<JsonArray> CatIndexAsync(string index, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get,
            $"_cat/indices/{Uri.EscapeDataString(index)}?format=json&bytes=b&h=health,status,index,docs.count,store.size", ct: ct);
        return res as JsonArray ?? new JsonArray();
    }

    public Task<JsonNode> GetMappingAsync(string index, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"{index}/_mapping", ct: ct)!;

    public async Task DeleteIndexAsync(string index, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, index, ct: ct);

    public Task<JsonNode> GetSettingsAsync(string index, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"{index}/_settings?flat_settings=true&filter_path=*.settings.index.max_result_window", ct: ct)!;

    public Task<JsonNode> GetStatsAsync(string index, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, $"{index}/_stats/docs", ct: ct)!;

    public async Task<JsonNode> GetDocumentAsync(string index, string id, CancellationToken ct = default)
    {
        // ES 对不存在的文档返回 404 且无 error 错误体（仅 found: false）
        try
        {
            return await SendAsync(HttpMethod.Get, $"{index}/_doc/{Uri.EscapeDataString(id)}", ct: ct)!;
        }
        catch (EsException ex) when (ex.StatusCode == 404)
        {
            throw new EsException("文档不存在（可能已被删除）", 404, "resource_not_found_exception");
        }
    }

    public Task<JsonNode> IndexDocAsync(string index, string? id, JsonObject doc, CancellationToken ct = default) =>
        string.IsNullOrEmpty(id)
            ? SendAsync(HttpMethod.Post, $"{index}/_doc", Serialize(doc), ct: ct)!
            : SendAsync(HttpMethod.Put, $"{index}/_doc/{Uri.EscapeDataString(id)}", Serialize(doc), ct: ct)!;

    public Task<JsonNode> PutDocAsync(string index, string id, JsonObject doc, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"{index}/_doc/{Uri.EscapeDataString(id)}", Serialize(doc), ct: ct)!;

    public async Task<JsonNode> DeleteDocAsync(string index, string id, CancellationToken ct = default)
    {
        // ES 对删除不存在的文档返回 404 且无 error 错误体（仅 result: not_found）
        try
        {
            return await SendAsync(HttpMethod.Delete, $"{index}/_doc/{Uri.EscapeDataString(id)}", ct: ct)!;
        }
        catch (EsException ex) when (ex.StatusCode == 404)
        {
            throw new EsException("文档不存在（可能已被删除）", 404, "resource_not_found_exception");
        }
    }

    public async Task<long> CountAsync(string index, JsonNode? query, CancellationToken ct = default)
    {
        var body = query != null ? new JsonObject { ["query"] = query.DeepClone() } : null;
        var res = await SendAsync(HttpMethod.Post, $"{index}/_count", Serialize(body), ct: ct);
        return res?["count"]?.GetValue<long>() ?? 0;
    }

    public Task<JsonNode> SearchAsync(string index, JsonObject body, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{index}/_search", body.ToJsonString(), ct: ct)!;

    public async Task<(JsonNode Body, string ScrollId)> SearchWithScrollAsync(
        string index, JsonObject body, string keepAlive, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Post, $"{index}/_search?scroll={keepAlive}", body.ToJsonString(), ct: ct);
        return (res!, res?["_scroll_id"]?.GetValue<string>() ?? "");
    }

    public async Task<(JsonNode Body, string ScrollId)> ScrollNextAsync(
        string scrollId, string keepAlive, CancellationToken ct = default)
    {
        var body = new JsonObject { ["scroll"] = keepAlive, ["scroll_id"] = scrollId };
        var res = await SendAsync(HttpMethod.Post, "_search/scroll", body.ToJsonString(), ct: ct);
        var newId = res?["_scroll_id"]?.GetValue<string>() ?? scrollId;
        return (res!, newId);
    }

    public Task ClearScrollAsync(string scrollId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["scroll_id"] = new JsonArray(scrollId) };
        return SendAsync(HttpMethod.Delete, "_search/scroll", body.ToJsonString(), ct: ct);
    }

    public Task<JsonNode> BulkAsync(string ndjson, string? index = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post,
            string.IsNullOrEmpty(index) ? "_bulk" : $"{index}/_bulk",
            ndjson, "application/x-ndjson", ct)!;

    public void Dispose() => _http.Dispose();
}
