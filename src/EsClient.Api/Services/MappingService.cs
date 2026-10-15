using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using Microsoft.Extensions.Caching.Memory;

namespace EsClient.Api.Services;

/// <summary>列元数据：扁平化后的字段路径 + 类型 + 可排序性 + keyword 子字段。</summary>
public record ColumnMeta(string Path, string Type, bool Sortable, string? KeywordPath)
{
    /// <summary>keyword/数值/date/boolean/ip 等可排序类型集合。</summary>
    public static readonly HashSet<string> SortableTypes = new()
    {
        "keyword", "long", "integer", "short", "byte", "double", "float",
        "half_float", "scaled_float", "unsigned_long", "date", "date_nanos",
        "boolean", "ip", "version"
    };
}

/// <summary>索引元数据。</summary>
public record IndexMeta(
    string Index, string Status, string Health, long DocsCount,
    int MaxResultWindow, List<ColumnMeta> Columns, List<string> NestedPaths);

/// <summary>mapping → 扁平列定义（递归 properties，深度上限 8）。</summary>
public class MappingService
{
    private const int MaxDepth = 8;

    private readonly ConnectionStore _conn;
    private readonly IMemoryCache _cache;

    public MappingService(ConnectionStore conn, IMemoryCache cache)
    {
        _conn = conn;
        _cache = cache;
    }

    /// <summary>取索引元数据（列、max_result_window、文档数），缓存 5 分钟。</summary>
    public async Task<IndexMeta> GetMetaAsync(string index, CancellationToken ct = default)
    {
        var client = _conn.Require();
        if (!IEsClient.IsValidIndexName(index))
            throw new EsException($"非法索引名：{index}", 400);

        var cacheKey = $"meta:{client.Info.Url}:{index}";
        if (_cache.TryGetValue(cacheKey, out IndexMeta? cached) && cached != null)
            return cached;

        // 状态/健康/文档数：_cat 单索引
        var catArr = await client.CatIndexAsync(index, ct);
        if (catArr.Count == 0)
            throw new EsException($"索引不存在：{index}", 404, "index_not_found_exception");
        var cat = (JsonObject)catArr[0]!;
        var status = cat["status"]?.GetValue<string>() ?? "open";
        var health = cat["health"]?.GetValue<string>() ?? "unknown";
        var docs = long.TryParse(cat["docs.count"]?.GetValue<string>(), out var d) ? d : 0;

        var maxWindow = 10000;
        if (status == "open")
        {
            var settings = await client.GetSettingsAsync(index, ct);
            var idxNode = settings is JsonObject so ? so[index] as JsonObject : null;
            var mw = idxNode?["settings"]?["index"]?["max_result_window"]?.ToString();
            if (int.TryParse(mw, out var mwVal) && mwVal > 0)
                maxWindow = mwVal;
        }

        var columns = new List<ColumnMeta>();
        var nestedPaths = new List<string>();
        if (status == "open")
        {
            var mapping = await client.GetMappingAsync(index, ct);
            if (mapping is JsonObject mObj &&
                mObj[index] is JsonObject idx &&
                idx["mappings"]?["properties"] is JsonObject props)
            {
                Flatten(props, "", 1, columns, nestedPaths);
            }

            // 兜底：mapping 无 properties 且有文档 → 采样推断列
            if (columns.Count == 0 && docs > 0)
            {
                var sample = await client.SearchAsync(index, new JsonObject
                {
                    ["size"] = 10,
                    ["query"] = new JsonObject { ["match_all"] = new JsonObject() },
                    ["sort"] = new JsonArray(new JsonObject { ["_doc"] = "asc" })
                }, ct);
                if (sample is JsonObject sObj && sObj["hits"]?["hits"] is JsonArray hits)
                {
                    var inferred = new Dictionary<string, ColumnMeta>();
                    foreach (var hitNode in hits)
                    {
                        if (hitNode is JsonObject hit && hit["_source"] is JsonObject src)
                            InferFromObject(src, "", 1, inferred);
                    }
                    columns.AddRange(inferred.Values);
                }
            }
        }

        var meta = new IndexMeta(index, status, health, docs, maxWindow, columns, nestedPaths);
        _cache.Set(cacheKey, meta, TimeSpan.FromMinutes(5));
        return meta;
    }

    /// <summary>索引被删除/重建后清除其元数据缓存。</summary>
    public void Invalidate(string index)
    {
        var client = _conn.Require();
        _cache.Remove($"meta:{client.Info.Url}:{index}");
    }

    private static void Flatten(JsonObject properties, string prefix, int depth, List<ColumnMeta> outCols,
        List<string> nestedPaths)
    {
        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject field) continue;
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            var type = field["type"]?.GetValue<string>();

            if (type == "nested")
                nestedPaths.Add(path);

            if (field["properties"] is JsonObject subProps)
            {
                if (depth >= MaxDepth)
                {
                    // 深度超限：整体作为 JSON 列，不再展开
                    outCols.Add(new ColumnMeta(path, "(json)", false, null));
                    continue;
                }
                Flatten(subProps, path, depth + 1, outCols, nestedPaths);
                continue;
            }

            type ??= "object";
            string? keywordPath = null;
            if (type == "text" && field["fields"] is JsonObject mf && mf["keyword"] is JsonObject kf)
            {
                var kType = kf["type"]?.GetValue<string>();
                if (kType == "keyword" || kType == null)
                    keywordPath = $"{path}.keyword";
            }
            outCols.Add(new ColumnMeta(path, type, ColumnMeta.SortableTypes.Contains(type), keywordPath));
        }
    }

    /// <summary>从采样文档推断列（深度上限同 Flatten，对象递归，数组取首个非空元素）。</summary>
    private static void InferFromObject(JsonObject obj, string prefix, int depth, Dictionary<string, ColumnMeta> cols)
    {
        foreach (var (name, node) in obj)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            switch (node)
            {
                case JsonObject child:
                    if (depth >= MaxDepth) { AddCol(cols, path, "(json)"); break; }
                    InferFromObject(child, path, depth + 1, cols);
                    break;
                case JsonArray arr:
                    InferArray(arr, path, depth, cols);
                    break;
                case JsonValue v:
                    AddCol(cols, path, InferType(v));
                    break;
            }
        }
    }

    private static void InferArray(JsonArray arr, string path, int depth, Dictionary<string, ColumnMeta> cols)
    {
        foreach (var item in arr)
        {
            if (item is null) continue;
            if (item is JsonObject o)
            {
                if (depth >= MaxDepth) { AddCol(cols, path, "(json)"); return; }
                InferFromObject(o, path, depth + 1, cols);
            }
            else if (item is JsonValue v)
            {
                AddCol(cols, path, InferType(v));
            }
            return;
        }
    }

    private static string InferType(JsonValue v)
    {
        var elem = v.GetValue<JsonElement>();
        return elem.ValueKind switch
        {
            JsonValueKind.Number => elem.TryGetInt64(out _) ? "long" : "double",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "keyword"
        };
    }

    private static void AddCol(Dictionary<string, ColumnMeta> cols, string path, string type)
    {
        if (!cols.ContainsKey(path))
            cols[path] = new ColumnMeta(path, type, ColumnMeta.SortableTypes.Contains(type), null);
    }
}
