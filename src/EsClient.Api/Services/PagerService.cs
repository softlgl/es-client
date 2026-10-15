using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using Microsoft.Extensions.Caching.Memory;

namespace EsClient.Api.Services;

/// <summary>单元格：V 为值；Kind 标记特殊渲染（json=对象/数组序列化，long=超长截断）。</summary>
public record Cell(object? V, string? Kind);

/// <summary>一行数据。</summary>
public record Row(string Id, List<Cell> Cells, JsonArray? Sort);

/// <summary>分页查询请求。</summary>
public record BrowseRequest(
    string Mode,            // from | after
    int From,               // from 模式偏移
    int Size,
    JsonArray? After,       // after 模式游标（上一页最后一行的 sort 值）
    QuerySpec? Query,
    SortSpec? Sort);

public record SortSpec(string Field, string Dir); // dir: asc | desc

/// <summary>分页查询响应。</summary>
public record BrowseResponse(
    List<Row> Rows,
    long? Total,
    string Mode,
    int? From,
    JsonArray? NextAfter);

/// <summary>统一分页执行器：from 模式（窗口内，可跳页）+ search_after 模式（深翻页）。</summary>
public class PagerService
{
    public const int MaxPageSize = 500;
    public const int DefaultPageSize = 50;
    public const int BrowseTruncateLength = 2000;

    private readonly ConnectionStore _conn;
    private readonly MappingService _mapping;
    private readonly QueryBuilder _queryBuilder;
    private readonly IMemoryCache _cache;

    public PagerService(ConnectionStore conn, MappingService mapping,
        QueryBuilder queryBuilder, IMemoryCache cache)
    {
        _conn = conn;
        _mapping = mapping;
        _queryBuilder = queryBuilder;
        _cache = cache;
    }

    public async Task<BrowseResponse> BrowseAsync(string index, BrowseRequest req, CancellationToken ct = default)
    {
        var client = _conn.Require();
        var meta = await _mapping.GetMetaAsync(index, ct);
        if (meta.Status != "open")
            throw new EsException($"索引 {index} 处于关闭（close）状态，无法浏览", 400);

        var size = Math.Clamp(req.Size <= 0 ? DefaultPageSize : req.Size, 1, MaxPageSize);
        var columns = meta.Columns;
        var colMap = columns.ToDictionary(c => c.Path, c => c);

        // 校验排序字段
        string? sortField = null;
        string sortDir = "asc";
        if (req.Sort != null && !string.IsNullOrEmpty(req.Sort.Field))
        {
            if (!colMap.TryGetValue(req.Sort.Field, out var sc) || !sc.Sortable)
                throw new EsException($"字段 {req.Sort.Field} 不支持排序", 400);
            sortField = sc.Type == "text" && sc.KeywordPath != null ? sc.KeywordPath : sc.Path;
            sortDir = req.Sort.Dir == "desc" ? "desc" : "asc";
        }

        var query = _queryBuilder.Build(req.Query, colMap);

        var body = new JsonObject
        {
            ["size"] = size,
            ["track_total_hits"] = false,
            ["sort"] = BuildSort(sortField, sortDir)
        };
        if (query != null) body["query"] = query.DeepClone();

        string mode;
        int? from = null;
        if (req.Mode == "after")
        {
            if (req.After is not { Count: > 0 })
                throw new EsException("游标翻页缺少 search_after 参数", 400);
            body["search_after"] = req.After.DeepClone();
            mode = "after";
        }
        else
        {
            var f = Math.Max(0, req.From);
            if ((long)f + size > meta.MaxResultWindow)
                throw new EsException(
                    $"偏移 {f} 超出索引 max_result_window（{meta.MaxResultWindow}），请使用游标翻页", 400);
            body["from"] = f;
            from = f;
            mode = "from";
        }

        var res = await client.SearchAsync(index, body, ct);
        var rows = ParseRows(res, columns);

        // 总数：无条件用 stats 缓存，有条件走 _count（带缓存）
        long? total = null;
        if (req.Query == null || req.Query.IsEmpty)
        {
            total = meta.DocsCount;
        }
        else
        {
            total = await CountCachedAsync(client, index, query!, ct);
        }

        var last = rows.Count > 0 ? rows[^1].Sort : null;
        return new BrowseResponse(rows, total, mode, from, last);
    }

    private static JsonArray BuildSort(string? field, string dir)
    {
        var arr = new JsonArray();
        if (field != null)
            arr.Add(new JsonObject { [field] = dir });
        arr.Add(new JsonObject { ["_doc"] = "asc" });
        return arr;
    }

    private static List<Row> ParseRows(JsonNode res, List<ColumnMeta> columns)
    {
        var rows = new List<Row>();
        if (res["hits"]?["hits"] is not JsonArray hits) return rows;

        foreach (var h in hits.OfType<JsonObject>())
        {
            var id = h["_id"]?.GetValue<string>() ?? "";
            var source = h["_source"] as JsonObject ?? new JsonObject();
            var cells = new List<Cell>(columns.Count);
            foreach (var col in columns)
                cells.Add(ExtractCell(source, col.Path));
            var sort = h["sort"] as JsonArray;
            rows.Add(new Row(id, cells, sort?.DeepClone().AsArray()));
        }
        return rows;
    }

    /// <summary>按列路径从 _source 取值：嵌套对象逐级下钻，遇数组/对象整体 JSON 化。</summary>
    private static Cell ExtractCell(JsonObject source, string path)
    {
        var segments = path.Split('.');
        JsonNode? cur = source;
        for (int i = 0; i < segments.Length; i++)
        {
            if (cur is not JsonObject obj) return new Cell(null, null);
            cur = obj[segments[i]];
            if (cur == null) return new Cell(null, null);
            if (cur is JsonArray) break; // 中途遇到数组：整体序列化
        }

        switch (cur)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                if (s.Length > BrowseTruncateLength)
                    return new Cell(s[..BrowseTruncateLength], "long");
                return new Cell(s, null);
            case JsonValue n when n.TryGetValue<decimal>(out _):
                return new Cell(n.ToJsonString(), null); // 数值原样字符串传递，避免精度丢失
            case JsonValue b when b.TryGetValue<bool>(out var bv):
                return new Cell(bv, null);
            case JsonObject or JsonArray:
                var js = cur.ToJsonString();
                return js.Length > BrowseTruncateLength
                    ? new Cell(js[..BrowseTruncateLength], "longjson")
                    : new Cell(js, "json");
            case JsonValue nv:
                return new Cell(nv.ToJsonString(), null);
            default:
                return new Cell(null, null);
        }
    }

    /// <summary>按条件计数（带缓存）。无条件时返回索引文档总数。</summary>
    public async Task<long> CountAsync(string index, QuerySpec? query, CancellationToken ct = default)
    {
        var client = _conn.Require();
        var meta = await _mapping.GetMetaAsync(index, ct);
        if (query == null || query.IsEmpty)
            return meta.DocsCount;
        var colMap = meta.Columns.ToDictionary(c => c.Path, c => c);
        var q = _queryBuilder.Build(query, colMap);
        if (q == null) return meta.DocsCount;
        return await CountCachedAsync(client, index, q, ct);
    }

    private async Task<long> CountCachedAsync(EsHttpClient client, string index, JsonNode query, CancellationToken ct)
    {
        var key = $"count:{client.Info.Url}:{index}:{query.ToJsonString()}";
        if (_cache.TryGetValue(key, out long cached))
            return cached;
        var count = await client.CountAsync(index, query, ct);
        _cache.Set(key, count, TimeSpan.FromSeconds(30));
        return count;
    }
}
