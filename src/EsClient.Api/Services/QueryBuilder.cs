using System.Text.Json.Nodes;
using EsClient.Api.Es;

namespace EsClient.Api.Services;

/// <summary>简单条件。</summary>
public record Condition(string Field, string Op, string? Value, string? Value2);

/// <summary>检索条件（简单条件 + 自由查询字符串 + 原生 DSL 三选一或组合）。</summary>
public record QuerySpec(List<Condition>? Conditions, string? QueryString, JsonNode? Dsl = null)
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
                var clause = BuildCondition(c, col);
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

    private JsonNode? BuildCondition(Condition c, ColumnMeta col)
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
                return RangeClause(col, c.Op, v, null);
            case "between":
                if (string.IsNullOrEmpty(c.Value2))
                    throw new EsException($"字段 {c.Field} 的范围条件需要两个值", 400);
                return RangeClause(col, "gte", v, c.Value2);
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
            leaf = col.KeywordPath != null
                ? new JsonObject { [col.KeywordPath] = value }
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
        string field;
        if (col.Type == "text")
        {
            if (col.KeywordPath != null)
                field = col.KeywordPath; // wildcard on keyword
            else
                return new JsonObject { ["match"] = new JsonObject { [col.Path] = value } };
        }
        else if (col.Type == "keyword" || col.Type == "ip" || col.Type == "version")
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
        string field;
        if (col.Type == "text")
        {
            if (col.KeywordPath != null)
                field = col.KeywordPath;
            else
                return new JsonObject { ["match_phrase_prefix"] = new JsonObject { [col.Path] = value } };
        }
        else
        {
            field = col.Path;
        }

        var p = new JsonObject { ["value"] = value };
        if (field == col.KeywordPath || col.Type == "keyword")
        {
            if (_conn.Active?.Info.SupportsCaseInsensitiveWildcard == true)
                p["case_insensitive"] = true;
        }
        return new JsonObject { ["prefix"] = new JsonObject { [field] = p } };
    }

    private static JsonObject RangeClause(ColumnMeta col, string op, string v1, string? v2)
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
                range["lte"] = v2;
                break;
        }
        // date 字段：用户输入为本地时间，显式指定时区避免被当 UTC
        if (col.Type == "date" || col.Type == "date_nanos")
        {
            var offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.Now);
            range["time_zone"] = (offset >= TimeSpan.Zero ? "+" : "-") +
                                 offset.Duration().ToString(@"hh\:mm");
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
