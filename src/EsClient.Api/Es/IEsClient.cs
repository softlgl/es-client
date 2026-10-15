using System.Text.Json.Nodes;

namespace EsClient.Api.Es;

/// <summary>连接元信息（版本探测后生成）。</summary>
public record ConnectionInfo(string Url, string ClusterName, string Version, int Major)
{
    /// <summary>wildcard/prefix 查询的 case_insensitive 参数需 ES 7.10+。</summary>
    public bool SupportsCaseInsensitiveWildcard => Major > 7 || (Major == 7 && ParseMinor(Version) >= 10);

    private static int ParseMinor(string v)
    {
        var parts = v.Split('.');
        return parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
    }
}

/// <summary>
/// ES 客户端统一抽象。查询 DSL 统一以 JSON 构造，7/8 行为差异在本层内消化，
/// 对上层暴露版本无关的 DTO。
/// </summary>
public interface IEsClient : IDisposable
{
    ConnectionInfo Info { get; }

    /// <summary>GET / — 探测集群信息（仅工厂在建连时使用）。</summary>
    Task<JsonObject> GetRootAsync(CancellationToken ct = default);

    /// <summary>GET _cat/indices?format=json — 索引列表（含 closed）。</summary>
    Task<JsonArray> CatIndicesAsync(CancellationToken ct = default);

    /// <summary>GET _cat/indices/{index} — 单索引信息。</summary>
    Task<JsonArray> CatIndexAsync(string index, CancellationToken ct = default);

    Task<JsonNode> GetMappingAsync(string index, CancellationToken ct = default);

    /// <summary>DELETE {index} — 删除索引。</summary>
    Task DeleteIndexAsync(string index, CancellationToken ct = default);

    /// <summary>GET {index}/_settings?filter_path=**.max_result_window</summary>
    Task<JsonNode> GetSettingsAsync(string index, CancellationToken ct = default);

    /// <summary>GET {index}/_stats/docs — 用于文档总数。</summary>
    Task<JsonNode> GetStatsAsync(string index, CancellationToken ct = default);

    Task<JsonNode> GetDocumentAsync(string index, string id, CancellationToken ct = default);

    /// <summary>新增文档（id 为空时自动生成），返回 ES 响应（_id/result）。</summary>
    Task<JsonNode> IndexDocAsync(string index, string? id, JsonObject doc, CancellationToken ct = default);

    /// <summary>全量替换文档。</summary>
    Task<JsonNode> PutDocAsync(string index, string id, JsonObject doc, CancellationToken ct = default);

    /// <summary>删除文档。</summary>
    Task<JsonNode> DeleteDocAsync(string index, string id, CancellationToken ct = default);

    Task<long> CountAsync(string index, JsonNode? query, CancellationToken ct = default);

    /// <summary>POST {index}/_search — body 为完整 DSL。</summary>
    Task<JsonNode> SearchAsync(string index, JsonObject body, CancellationToken ct = default);

    /// <summary>POST {index}/_search?scroll=keepAlive — 返回响应与 scroll_id。</summary>
    Task<(JsonNode Body, string ScrollId)> SearchWithScrollAsync(
        string index, JsonObject body, string keepAlive, CancellationToken ct = default);

    /// <summary>POST /_search/scroll — 每次续期。</summary>
    Task<(JsonNode Body, string ScrollId)> ScrollNextAsync(
        string scrollId, string keepAlive, CancellationToken ct = default);

    Task ClearScrollAsync(string scrollId, CancellationToken ct = default);

    /// <summary>POST /_bulk — ndjson 原文。</summary>
    Task<JsonNode> BulkAsync(string ndjson, string? index = null, CancellationToken ct = default);

    /// <summary>索引名合法性：仅字母、数字、_ . - ，且不以 _ 开头。</summary>
    static bool IsValidIndexName(string name) =>
        !string.IsNullOrEmpty(name) && name[0] != '_' &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
