using System.Text.Json.Nodes;

namespace EsClient.Api.Controllers;

/// <summary>
/// 模型输出 → 可执行请求体的解析与安全约束。
/// 两条 AI 链路都要从自由文本里抠出 JSON 并施加护栏，此前各写了一份逐行相同的
/// 围栏剥离，外加各自散落在控制器里的白名单与钳制逻辑。集中后有两点好处：
/// 围栏/截取规则只有一份（不会再分叉），且护栏本身是纯函数、不依赖 HttpContext，
/// 可以脱离 Web 宿主直接测试 —— 它挡的是模型幻觉与脚本注入，不该埋在控制器里。
///
/// 注意两条链路的策略差异是**有意保留**的：
/// 问答链路（ExtractSearchBody）执行前会过白名单并拒绝 script；
/// 生成链路（ExtractQueryDsl）的产物是回填给浏览页由用户确认后再执行，
/// 且其提示词已明令禁止 script，故沿用原有行为不做额外拦截。
/// </summary>
internal static class AiDslGuard
{
    /// <summary>剥 markdown 围栏，返回其中第一个以「{」开头的块；无有效围栏则原样返回。</summary>
    public static string StripFences(string text)
    {
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        while (fenceStart >= 0)
        {
            var lineEnd = text.IndexOf('\n', fenceStart);
            if (lineEnd < 0) break;
            var fenceEnd = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (fenceEnd < 0) break;
            var inner = text[(lineEnd + 1)..fenceEnd].Trim();
            if (inner.StartsWith('{')) return inner;
            fenceStart = text.IndexOf("```", fenceEnd + 3, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// 剥围栏 → 截取最外层 {...} → 解析为 JSON 对象。
    /// 返回 <c>(节点, 错误提示)</c>，成功时错误为 null；失败原因分「不是有效 JSON」
    /// 与「不是 JSON 对象」两种，前端据此给出不同的自愈提示，不要合并成一句。
    /// </summary>
    public static (JsonObject? Node, string? Error) TryParseObject(string content)
    {
        var text = StripFences(content.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, "AI 返回的内容不是有效 JSON");
        JsonNode? node;
        try { node = JsonNode.Parse(text[start..(end + 1)]); }
        catch { return (null, "AI 返回的内容不是有效 JSON"); }
        if (node is not JsonObject obj) return (null, "AI 返回的内容不是 JSON 对象");
        return (obj, null);
    }

    /// <summary>
    /// 递归检查是否含脚本注入点：script / runtime_mappings / runtime_fields，
    /// 以及以它们为入口的 script_score、script_fields 等一切形式。
    /// </summary>
    public static bool HasScriptKey(JsonNode node)
    {
        if (node is JsonObject obj)
            return obj.Any(kvp => kvp.Key.EqualsIgnoreCase("script")
                || kvp.Key.EqualsIgnoreCase("runtime_mappings")
                || kvp.Key.EqualsIgnoreCase("runtime_fields"))
                || obj.Any(kvp => kvp.Value != null && HasScriptKey(kvp.Value));
        if (node is JsonArray arr)
            return arr.Any(n => n != null && HasScriptKey(n));
        return false;
    }

    /// <summary>
    /// 递归钳制 terms 聚合的 size（上限 <paramref name="max"/>，下限 1）。
    /// query 的 size 有硬上限，聚合侧此前完全依赖提示词自律；模型在高基数字段上
    /// 给出大 size 的 terms 会让 ES 长时间占用分片缓存，与 size 约束形成不对称保护。
    /// 只改显式写了非法值的节点，不给缺省的 size 赋值，避免无谓地改写模型输出。
    /// </summary>
    public static void ClampTermsSize(JsonNode node, int max = 50)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["terms"] is JsonObject terms && terms["field"] != null)
                    ClampOne(terms);
                // 多字段 terms：{"terms":[{"field":"a"},{"field":"b"}]}
                if (obj["terms"] is JsonArray termsArr)
                    foreach (var t in termsArr.OfType<JsonObject>())
                        ClampOne(t);
                foreach (var (_, v) in obj) if (v != null) ClampTermsSize(v, max);
                break;
            case JsonArray arr:
                foreach (var v in arr) if (v != null) ClampTermsSize(v, max);
                break;
        }

        return;

        void ClampOne(JsonObject terms)
        {
            if (terms["size"] is JsonValue v && v.TryGetValue<int>(out var raw) && (raw < 1 || raw > max))
                terms["size"] = Math.Clamp(raw, 1, max);
        }
    }

    private static readonly string[] AllowedRootKeys = ["query", "aggs", "aggregations", "size", "sort", "_source", "track_total_hits"];

    /// <summary>
    /// 问答链路：从模型输出提取 search 请求体。
    /// 顺序：剥围栏 → 截 JSON → 顶层键白名单 → 禁 script → 限 size → 钳 terms。
    /// </summary>
    public static (JsonObject? Body, string? Error) ExtractSearchBody(string content)
    {
        var (obj, err) = TryParseObject(content);
        if (obj == null) return (null, err);

        foreach (var key in obj.Select(kvp => kvp.Key).ToList())
            if (!AllowedRootKeys.Contains(key))
            {
                _ = obj.Remove(key); // 静默丢弃无关顶层键，而非整体拒绝
            }
        if (obj["aggregations"] != null && obj["aggs"] == null)
            obj["aggs"] = obj["aggregations"]!.DeepClone();
        obj.Remove("aggregations");

        if (obj["query"] == null && obj["aggs"] == null)
            return (null, "生成的 DSL 缺少 query 或 aggs 子句");
        if (HasScriptKey(obj))
            return (null, "为安全起见问答不支持 script，请改用普通聚合/查询");

        // size 约束：缺省 0（聚合优先），上限 10（明细展示用）
        int size = 0;
        try { size = obj["size"]?.GetValue<int>() ?? 0; } catch { /* 非数值按 0 处理 */ }
        if (size is < 0 or > 10) size = Math.Clamp(size, 0, 10);
        obj["size"] = size;
        obj["track_total_hits"] = true;

        ClampTermsSize(obj);

        return (obj, null);
    }

    /// <summary>
    /// 问答链路：识别模型标记的「无需查询」。生成阶段允许模型对与索引数据无关的问题
    /// （常识/观点/闲聊）输出 {"no_query":true}，控制器据此跳过执行、改用通用回答。
    /// 只认显式布尔 true，避免把缺少 query/aggs 的残缺 DSL 误判为免查询。
    /// </summary>
    public static bool IsNoQueryMarker(string content)
    {
        var (obj, _) = TryParseObject(content);
        return obj?["no_query"] is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;
    }

    private static readonly string[] QueryRootKeys =
    [
        "bool", "match", "match_phrase", "match_phrase_prefix", "multi_match", "term", "terms",
        "range", "exists", "wildcard", "prefix", "fuzzy", "ids", "nested", "query_string",
        "match_all", "match_none", "function_score", "simple_query_string", "regexp", "span_near"
    ];

    /// <summary>
    /// 生成链路：从模型输出提取查询 DSL。
    /// query/sort/size/aggs/_source 等顶层键全部保留，避免丢失模型的排序与取数条数语义。
    /// </summary>
    public static JsonObject? ExtractQueryDsl(string content)
    {
        if (TryParseObject(content).Node is not JsonObject obj) return null;

        // 已含 query：整体保留（sort/size/aggs 等顶层键随行）
        if (obj["query"] is JsonObject)
            return (JsonObject)obj.DeepClone();

        // 裸 query 子句：包一层 query，同级 sort 一并提级
        if (obj.Any(kvp => QueryRootKeys.Contains(kvp.Key)))
        {
            JsonNode? sort = null;
            if (obj["sort"] != null) { sort = obj["sort"]!.DeepClone(); obj.Remove("sort"); }
            var wrapped = new JsonObject { ["query"] = obj.DeepClone() };
            if (sort != null) wrapped["sort"] = sort;
            return wrapped;
        }

        // 模型省略 query 只给了 sort/size/aggs：补 match_all 保住语义
        if (obj.Any(kvp => kvp.Key is "sort" or "size" or "aggs" or "aggregations" or "_source"))
        {
            obj["query"] = new JsonObject { ["match_all"] = new JsonObject() };
            return (JsonObject)obj.DeepClone();
        }

        return null;
    }
}

file static class JsonNodeKeyExtensions
{
    /// <summary>键名大小写不敏感比较：模型可能输出 Script / SCRIPT。</summary>
    public static bool EqualsIgnoreCase(this string? a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}