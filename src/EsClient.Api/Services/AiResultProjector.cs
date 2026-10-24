using System.Text.Json.Nodes;
using EsClient.Api.Es;

namespace EsClient.Api.Services;

/// <summary>
/// ES search 响应 → 两种下游形态的投影（纯函数，不依赖 HttpContext，可脱离 Web 宿主直接测试）：
/// ① <see cref="TrimForModel"/>：裁剪成紧凑 JSON 喂模型总结或工具回填（桶数、明细行数、单元格长度可控）；
/// ② <see cref="BuildTableData"/>：转成前端可重放的表格（聚合分布 / 单值指标 / 明细命中）。
/// 此前「喂模型的裁剪」在 AiChatController 与 AiTools 各写一份、阈值不同且工具版丢了 terms 截断告警，
/// 「表格构造」整块埋在控制器里既不可测也非控制器职责，集中到此处后两条链路共用同一份裁剪与告警逻辑。
/// </summary>
internal static class AiResultProjector
{
    // ---------- ① 喂模型的紧凑 JSON ----------

    /// <summary>
    /// 把执行结果裁剪成喂给模型的紧凑 JSON：聚合桶 ≤<paramref name="bucketCap"/>、明细行 ≤<paramref name="rowCap"/>、
    /// 单元格值截断到 <paramref name="cellMax"/> 字符。terms 分桶被截时补 _note（优先 ES 原生 sum_other_doc_count，
    /// 缺失时退化为「返回桶数超过上限」）——截断下模型若拿已列桶除以 total 算占比会得出错误结论。
    /// </summary>
    public static JsonObject TrimForModel(JsonNode res, int bucketCap, int rowCap, int cellMax)
    {
        var outNode = new JsonObject { ["total"] = res["hits"]?["total"]?["value"]?.DeepClone() ?? 0 };

        if (res["aggregations"] is JsonObject aggs)
        {
            var trimmed = new JsonObject();
            foreach (var (name, agg) in aggs)
            {
                if (agg?["buckets"] is JsonArray buckets)
                {
                    var kept = new JsonArray(buckets.Take(bucketCap).Select(b => b!.DeepClone()).ToArray());
                    var other = agg["sum_other_doc_count"] is JsonValue ov && ov.TryGetValue<long>(out var oc) ? oc : 0;
                    if (other > 0)
                        kept.Add(new JsonObject { ["_note"] = $"（terms 分桶不完整：另有 {other:N0} 条文档未纳入以上分桶）" });
                    else if (buckets.Count > bucketCap)
                        kept.Add(new JsonObject { ["_note"] = $"（其余 {buckets.Count - bucketCap} 个桶已省略）" });
                    trimmed[name] = new JsonObject { ["buckets"] = kept };
                }
                else
                {
                    trimmed[name] = agg!.DeepClone();
                }
            }
            outNode["aggregations"] = trimmed;
        }

        if (res["hits"]?["hits"] is JsonArray hits && hits.Count > 0)
        {
            var rows = new JsonArray();
            foreach (var h in hits.Take(rowCap))
            {
                var row = new JsonObject { ["_id"] = h?["_id"]?.DeepClone() };
                if (h?["_source"] is JsonObject src)
                    foreach (var (k, v) in src)
                    {
                        // 字符串值直接取原文：ToJsonString 会把已是 JSON 的字段值（日志类索引常见）再序列化
                        // 一层，转义引号 \" 泄进展示与模型文本
                        var s = CellText(v) ?? "null";
                        if (s.Length > cellMax) s = s[..cellMax] + "…";
                        row[k] = s;
                    }
                rows.Add(row);
            }
            outNode["rows"] = rows;
        }
        return outNode;
    }

    // ---------- ② 前端可重放的表格 ----------

    /// <summary>
    /// 在聚合定义树里按名称定位 terms 聚合的字段（支持 filter/nested 等包裹层）。
    /// ES 的聚合响应只回 buckets、不回显聚合定义，字段名只能从生成阶段的 body 里取。
    /// </summary>
    private static string? FindTermsField(JsonNode? aggsNode, string name)
    {
        if (aggsNode is not JsonObject obj) return null;
        foreach (var (k, v) in obj)
        {
            if (v is not JsonObject child) continue;
            if (k == name && child["terms"]?["field"] is JsonValue fv && fv.TryGetValue<string>(out var fs))
                return fs;
            // 单层包裹（filter/nested 等）里再找同名聚合
            if (child["aggs"] is JsonObject inner)
            {
                var hit = FindTermsField(inner, name);
                if (hit != null) return hit;
            }
        }
        return null;
    }

    /// <summary>
    /// 从首个桶探测单值指标列：cardinality/avg/sum/min/max/value_count 等子聚合的响应带 "value"，
    /// 取子聚合名作列名（如 cardinality 的 distinct_ip → 列名 distinct_ip）。桶内嵌 buckets
    /// （多级 terms/date_histogram）的跳过。一个都没有时调用方退回 doc_count。
    /// </summary>
    private static List<string> PickMetricNames(JsonNode? firstBucket)
    {
        var names = new List<string>();
        if (firstBucket is not JsonObject obj) return names;
        foreach (var (k, v) in obj)
        {
            if (k is "key" or "key_as_string" or "doc_count") continue;
            if (v is JsonObject vo && vo["value"] != null)
                names.Add(k);
        }
        return names;
    }

    /// <summary>桶键：优先 key_as_string（date_histogram 的 format 产物），漏带 format 时把 epoch 毫秒兜底格式化为日期。</summary>
    private static JsonNode BucketKey(JsonNode? b, string? dateFormat)
    {
        var key = (b?["key_as_string"] ?? b?["key"])?.DeepClone() ?? JsonValue.Create("");
        if (dateFormat != null && key is JsonValue dv && dv.TryGetValue<long>(out var epochMs))
            key = JsonValue.Create(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToString(dateFormat));
        return key;
    }

    /// <summary>
    /// 探测首个桶内的多级子聚合：某个子聚合的值自带 buckets（父桶下再嵌桶，如 date_histogram 里的
    /// top_user terms）。返回（子聚合名，子桶内的单值指标列）；没有则 null（调用方按单层处理）。
    /// </summary>
    private static (string Name, List<string> Metrics)? FindNestedBucketAgg(JsonNode? firstBucket)
    {
        if (firstBucket is not JsonObject obj) return null;
        foreach (var (k, v) in obj)
        {
            if (k is "key" or "key_as_string" or "doc_count") continue;
            if (v is JsonObject vo && vo["buckets"] is JsonArray { Count: > 0 } sub)
                return (k, PickMetricNames(sub[0]));
        }
        return null;
    }

    /// <summary>单值指标值格式化：整数加千分位；小数原样保留精度（avg 等）。</summary>
    private static JsonNode FormatMetricValue(JsonNode? value)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return JsonValue.Create(l.ToString("N0"));
            var s = v.ToJsonString().Trim('"');
            if (!string.IsNullOrEmpty(s)) return JsonValue.Create(s);
        }
        return JsonValue.Create("");
    }

    /// <summary>
    /// 取该聚合对应 date_histogram 的日期格式（支持 filter/nested 等单层包裹），不是则返回 null。
    /// 用于桶键缺 key_as_string 时兜底把 epoch 毫秒格式化为日期——模型偶尔不遵守提示词里的
    /// format 规则，表格会退化成时间戳。粒度细到小时/分钟时才带时分。
    /// </summary>
    private static string? DateHistogramFormat(JsonNode? aggsNode, string name)
    {
        if (aggsNode is not JsonObject obj) return null;
        foreach (var (k, v) in obj)
        {
            if (v is not JsonObject child) continue;
            if (k == name && child["date_histogram"] is JsonObject dh)
                return IsSubDay(dh["calendar_interval"]) ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";
            // 单层包裹（filter/nested 等）里再找同名聚合
            if (child["aggs"] is JsonObject inner && DateHistogramFormat(inner, name) is { } innerFmt)
                return innerFmt;
        }
        return null;

        static bool IsSubDay(JsonNode? interval) =>
            interval is JsonValue iv && iv.TryGetValue<string>(out var s) &&
            s is "second" or "minute" or "hour" or "1m" or "5m" or "15m" or "30m" or "1h";
    }

    /// <summary>
    /// 为前端生成可重放的表格数据：terms/date_histogram 分布 → table；数值聚合 → value；明细命中 → table。
    /// 分布表首列桶值优先取 key_as_string（date_histogram 的 format 产物），数值列取桶内单值子聚合
    /// （如 cardinality），没有时才显示文档数。terms 分布会带上聚合字段（aggField）与其 nested 路径，
    /// 前端据此把桶值做成下钻入口。
    /// 父桶内再嵌桶（多级聚合，如 date_histogram 下挂 top_user terms）时，按 父桶 × 子桶 展开成行，
    /// 否则表格只会显示父桶文档数、丢掉子桶里的真实答案。
    /// </summary>
    public static JsonObject? BuildTableData(JsonNode res, JsonObject body, IndexMeta meta)
    {
        var aggs = res["aggregations"] as JsonObject;
        if (aggs != null)
        {
            foreach (var (name, agg) in aggs)
            {
                if (agg?["buckets"] is not JsonArray buckets || buckets.Count == 0) continue;
                // 桶内单值子聚合（cardinality/avg/sum/min/max/value_count）作为数值列，列名取子聚合名；
                // 没有时退回 doc_count。否则「每天独立 IP 数」这类问题表格只会显示文档数，与问题口径不符
                var metrics = PickMetricNames(buckets[0]);
                var dateFormat = DateHistogramFormat(body["aggs"] ?? body["aggregations"], name);
                var subAgg = FindNestedBucketAgg(buckets[0]);
                var rows = new JsonArray();
                string? note = null;
                JsonArray columns;
                if (subAgg != null)
                {
                    // 多级聚合：把每个父桶的子桶逐个展开成行（父级值重复），典型「每天访问最多的用户」
                    var (subName, subMetrics) = subAgg.Value;
                    columns = new JsonArray([name, subName]);
                    if (subMetrics.Count > 0) foreach (var m in subMetrics) columns.Add(m);
                    else columns.Add("次数");
                    // 交叉展开可能堆成上百行（8 天 × 每桶 10 controller），设硬上限防刷屏
                    const int MaxRows = 60;
                    bool capped = false;
                    foreach (var b in buckets.Take(20))
                    {
                        if (capped) break;
                        var key = BucketKey(b, dateFormat);
                        if (b?[subName]?["buckets"] is not JsonArray { Count: > 0 } sub)
                        {
                            // 该父桶无子桶：补一行占位（父键 + 父文档数），免得整个父级从表格消失
                            var only = new JsonArray([key, ""]);
                            only.Add((b?["doc_count"]?.GetValue<long>() ?? 0).ToString("N0"));
                            rows.Add(only);
                            continue;
                        }
                        foreach (var sb in sub.Take(10))
                        {
                            // key 每个父桶只算一次但会被多行复用，JsonNode 不允许挂多个父→逐行 DeepClone
                            var cells = new JsonArray([key.DeepClone(), (sb?["key_as_string"] ?? sb?["key"])?.DeepClone() ?? JsonValue.Create("")]);
                            if (subMetrics.Count > 0)
                                foreach (var m in subMetrics) cells.Add(FormatMetricValue(sb?[m]?["value"]));
                            else
                                cells.Add((sb?["doc_count"]?.GetValue<long>() ?? 0).ToString("N0"));
                            rows.Add(cells);
                            if (rows.Count >= MaxRows) { capped = true; break; }
                        }
                    }
                    if (capped) note = $"交叉展开行数较多，此处仅展示前 {MaxRows} 行（完整数据可在数据浏览中按同一查询执行）";
                }
                else
                {
                    foreach (var b in buckets.Take(20))
                    {
                        // date_histogram 指定 format 时 ES 额外返回 key_as_string（如 2026-10-01），
                        // 而 key 恒为 epoch 毫秒；优先取格式化值，terms 桶无此字段再退回 key。
                        var key = BucketKey(b, dateFormat);
                        var cells = new JsonArray([key]);
                        if (metrics.Count > 0)
                            foreach (var m in metrics) cells.Add(FormatMetricValue(b?[m]?["value"]));
                        else
                            cells.Add((b?["doc_count"]?.GetValue<long>() ?? 0).ToString("N0"));
                        rows.Add(cells);
                    }
                    columns = new JsonArray([name]);
                    if (metrics.Count > 0)
                        foreach (var m in metrics) columns.Add(m);
                    else
                        columns.Add("文档数");
                }
                var table = new JsonObject
                {
                    ["kind"] = "table",
                    ["columns"] = columns,
                    ["rows"] = rows
                };
                if (note != null) table["note"] = note;
                var aggField = FindTermsField(body["aggs"] ?? body["aggregations"], name);
                if (aggField != null)
                {
                    table["aggField"] = aggField;
                    // nested 路径下的字段必须用 nested 查询包裹，否则 term 匹配不到嵌套文档
                    var nested = meta.ResolveNestedPath(aggField);
                    if (nested != null) table["aggNested"] = nested;
                    // 桶值下钻需带上产生该聚合的原始 query（否则下钻会丢掉筛选条件）。
                    // 写在 data 上而非仅依赖最终渲染的 DSL 折叠区，流式总结期间抢点也能取到。
                    // 无 query（纯 aggs 的 match_all）则不写，前端自行回退。
                    if (body["query"] != null) table["baseQuery"] = body["query"]!.DeepClone();
                }
                return table;
            }

            // 无桶聚合：尝试单值指标（avg/max/min/sum/value_count）
            var metricRows = new JsonArray();
            foreach (var (name, agg) in aggs)
            {
                var val = agg?["value"];
                if (val != null && (val is JsonValue || val is JsonObject))
                {
                    var s = CellText(val is JsonObject vo ? vo["value"] : val);
                    if (!string.IsNullOrWhiteSpace(s)) metricRows.Add(new JsonArray([name, s!]));
                }
            }
            if (metricRows.Count > 0)
                return new JsonObject { ["kind"] = "value", ["columns"] = new JsonArray(["指标", "值"]), ["rows"] = metricRows };
            return null;
        }

        // 明细命中：取首个 _source 的键做列（≤6 列）
        var hitsArr = res["hits"]?["hits"] as JsonArray;
        if (hitsArr is not { Count: > 0 }) return null;
        var cols = new List<string>();
        foreach (var h in hitsArr.Take(1))
            if (h?["_source"] is JsonObject src0)
                cols.AddRange(src0.Select(kvp => kvp.Key).Take(6));
        if (cols.Count == 0) return null;
        var tableRows = new JsonArray();
        foreach (var h in hitsArr.Take(10))
        {
            var src = h?["_source"] as JsonObject;
            var r = new JsonArray();
            foreach (var c in cols)
            {
                var s = CellText(src?[c]) ?? "";
                // 前端表格是纯展示面：源文档字段本身存 JSON 字符串时解开一层，\" 不再泄进单元格
                s = JsonDisplayUnwrapper.Unwrap(s);
                if (s.Length > 60) s = s[..60] + "…";
                r.Add(s);
            }
            tableRows.Add(r);
        }
        return new JsonObject
        {
            ["kind"] = "table",
            ["columns"] = new JsonArray(cols.Select(c => (JsonNode)c).ToArray()),
            ["rows"] = tableRows,
            // 带上命中总数与是否截断：问答表格只展示前若干行，用户需要一个基准
            // 才能判断与数据浏览的结果是否一致（浏览页按同一份 DSL 执行）
            ["total"] = res["hits"]?["total"]?["value"]?.DeepClone() ?? 0,
            ["truncated"] = (res["hits"]?["total"]?["value"]?.GetValue<long>() ?? 0) > tableRows.Count
        };
    }

    /// <summary>
    /// 单元格取文本：字符串值直接给原文（不重走 JSON 序列化，避免字段值本身是 JSON 时
    /// 被叠一层 \" 转义泄进展示）；数组/对象等非字符串值仍是合法 JSON，序列化展示。
    /// </summary>
    private static string? CellText(JsonNode? v) =>
        v is null ? null
        : v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s
        : v.ToJsonString();
}
