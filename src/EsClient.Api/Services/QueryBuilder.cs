using System.Text.Json.Nodes;
using EsClient.Api.Es;

namespace EsClient.Api.Services;

/// <summary>简单条件。</summary>
public record Condition(string Field, string Op, string? Value, string? Value2);

/// <summary>检索条件（简单条件 + 自由查询字符串 + 原生 DSL 三选一或组合）。</summary>
public record QuerySpec(List<Condition>? Conditions, string? QueryString, JsonNode? Dsl = null, string? TimeZone = null)
{
    public bool IsEmpty =>
        (Conditions == null || Conditions.Count == 0) &&
        string.IsNullOrWhiteSpace(QueryString) &&
        Dsl == null;
}

/// <summary>条件/自由查询 → ES bool DSL。浏览与导出共用。</summary>
public class QueryBuilder
{
    private readonly ConnectionStore _conn;

    public QueryBuilder(ConnectionStore conn) => _conn = conn;

    /// <summary>构造 query 子句；无条件时返回 null（match_all）。</summary>
    public JsonNode? Build(QuerySpec? spec, Dictionary<string, ColumnMeta> columns)
    {
        if (spec == null || spec.IsEmpty) return null;

        // AI 生成/用户直写的原生 DSL（完整请求体 { query: ... }）：取内层 query 作为子句
        if (spec.Dsl != null)
        {
            var q = spec.Dsl["query"];
            if (q == null) throw new EsException("DSL 缺少 query 子句", 400);
            return q.DeepClone();
        }

        var must = new List<JsonNode>();

        if (spec.Conditions != null)
        {
            foreach (var c in spec.Conditions)
            {
                if (string.IsNullOrWhiteSpace(c.Field)) continue;
                if (!columns.TryGetValue(c.Field, out var col))
                    throw new EsException($"未知字段：{c.Field}", 400);
                var clause = BuildCondition(c, col, spec.TimeZone);
                if (clause != null) must.Add(clause);
            }
        }

        var qs = spec.QueryString?.Trim();
        if (!string.IsNullOrEmpty(qs))
        {
            must.Add(new JsonObject
            {
                ["query_string"] = new JsonObject
                {
                    ["query"] = qs,
                    ["lenient"] = true
                }
            });
        }

        if (must.Count == 0) return null;
        if (must.Count == 1) return must[0];
        return new JsonObject { ["bool"] = new JsonObject { ["must"] = new JsonArray([.. must]) } };
    }

    private JsonNode? BuildCondition(Condition c, ColumnMeta col, string? timeZone)
    {
        var v = c.Value ?? "";
        switch (c.Op)
        {
            case "exists":
                return ExistsClause(col.Path, true);
            case "missing":
                return ExistsClause(col.Path, false);
        }

        if (string.IsNullOrEmpty(v) && c.Op != "between")
            throw new EsException($"字段 {c.Field} 的条件值为空", 400);

        switch (c.Op)
        {
            case "eq":
                return TermClause(col, v, negate: false);
            case "ne":
                return TermClause(col, v, negate: true);
            case "contains":
                return ContainsClause(col, v);
            case "prefix":
                return PrefixClause(col, v);
            case "gt": case "gte": case "lt": case "lte":
                return RangeClause(col, c.Op, v, null, timeZone);
            case "between":
                if (string.IsNullOrEmpty(c.Value2))
                    throw new EsException($"字段 {c.Field} 的范围条件需要两个值", 400);
                // 必须传 "between"：传 "gte" 会命中 RangeClause 的 gte 分支，
                // 「止」被静默丢弃，区间退化成只有下限 —— 命中数偏多，且用户无从察觉
                return RangeClause(col, "between", v, c.Value2, timeZone);
            default:
                throw new EsException($"不支持的操作符：{c.Op}", 400);
        }
    }

    /// <summary>term 匹配字段选择：text 优先 keyword 子字段，无子字段则用 match。</summary>
    private static JsonObject TermClause(ColumnMeta col, string value, bool negate)
    {
        JsonNode leaf;
        if (col.Type == "text")
        {
            // text 带 keyword 子字段 → term 精确匹配（需整段值相等）。
            // 曾因漏写外层 "term" 包裹而生成 {"a.keyword":"v"} 这种非法子句，导致 ES 报 parsing_exception。
            leaf = col.KeywordPath != null
                ? new JsonObject { ["term"] = new JsonObject { [col.KeywordPath] = value } }
                : new JsonObject { ["match"] = new JsonObject { [col.Path] = value } };
        }
        else
        {
            leaf = new JsonObject { ["term"] = new JsonObject { [col.Path] = value } };
        }
        if (!negate) return (JsonObject)leaf;
        return new JsonObject { ["bool"] = new JsonObject { ["must_not"] = new JsonArray(leaf) } };
    }

    private JsonNode ContainsClause(ColumnMeta col, string value)
    {
        // text：用 match_phrase 走分析后的主字段。
        // 曾用 wildcard on .keyword，但 keyword 子字段常带 ignore_above(默认 256)，
        // 超长值根本不进索引 → 长文本「包含」会静默漏查（日志类 message 尤其明显）。
        // 代价：语义从「真子串」变为「分词后连续词组」，词内切片（如 refus 匹配 error）不再命中。
        if (col.Type == "text")
            return new JsonObject { ["match_phrase"] = new JsonObject { [col.Path] = value } };

        string field;
        if (col.Type == "keyword" || col.Type == "ip" || col.Type == "version")
        {
            field = col.Path;
        }
        else
        {
            // 数值/日期等类型没有"包含"语义，退化为 term
            return TermClause(col, value, negate: false);
        }

        var w = new JsonObject { ["value"] = $"*{value}*" };
        if (_conn.Active?.Info.SupportsCaseInsensitiveWildcard == true)
            w["case_insensitive"] = true;
        return new JsonObject { ["wildcard"] = new JsonObject { [field] = w } };
    }

    private JsonNode PrefixClause(ColumnMeta col, string value)
    {
        // text：用 match_phrase_prefix 走分析后的主字段。
        // 曾用 prefix on .keyword，与「包含」同源——keyword 子字段 ignore_above 会截断长值而漏查。
        if (col.Type == "text")
            return new JsonObject { ["match_phrase_prefix"] = new JsonObject { [col.Path] = value } };

        string field;
        if (col.Type == "keyword" || col.Type == "ip" || col.Type == "version")
        {
            field = col.Path;
        }
        else
        {
            // 数值/日期等类型没有「前缀」语义：prefix 打在 points 索引（无 terms）会静默返回空，
            // 与 ContainsClause 对称，退化为 term 精确匹配。
            return TermClause(col, value, negate: false);
        }

        var p = new JsonObject { ["value"] = value };
        if (_conn.Active?.Info.SupportsCaseInsensitiveWildcard == true)
            p["case_insensitive"] = true;
        return new JsonObject { ["prefix"] = new JsonObject { [field] = p } };
    }

    private static JsonObject RangeClause(ColumnMeta col, string op, string v1, string? v2, string? timeZone)
    {
        var range = new JsonObject();
        switch (op)
        {
            case "gt": range["gt"] = v1; break;
            case "gte": range["gte"] = v1; break;
            case "lt": range["lt"] = v1; break;
            case "lte": range["lte"] = v1; break;
            case "between":
                range["gte"] = v1;
                if (v2 != null) range["lte"] = v2;
                break;
        }
        // date 字段：用户输入为本地时间，显式指定时区避免被当 UTC。
        // 前端传入浏览器 IANA 时区时优先用它（与手动/AI 两条路径口径一致）；
        // 缺省再回落服务器本地偏移（旧行为）。
        if (col.Type == "date" || col.Type == "date_nanos")
        {
            if (!string.IsNullOrWhiteSpace(timeZone))
                range["time_zone"] = timeZone.Trim();
            else
            {
                var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
                range["time_zone"] = (offset >= TimeSpan.Zero ? "+" : "-") +
                                     offset.Duration().ToString(@"hh\:mm");
            }
        }
        return new JsonObject { ["range"] = new JsonObject { [col.Path] = range } };
    }

    private static JsonObject ExistsClause(string field, bool positive)
    {
        var leaf = new JsonObject { ["exists"] = new JsonObject { ["field"] = field } };
        if (positive) return leaf;
        return new JsonObject { ["bool"] = new JsonObject { ["must_not"] = new JsonArray(leaf) } };
    }
}
