using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsClient.Api.Controllers;

/// <summary>
/// AI 问答：针对当前索引数据的取数型问答（平均、计数、分布、Top N 等）。
/// 流程：问题 + mapping + 字段采样 → 生成聚合/查询 DSL → 校验（只读、限 size）→ 执行 →
/// 结果喂回模型总结。执行失败自动带错误重试一次。会话历史由 ChatStore 持久化到服务端。
/// </summary>
[ApiController]
public class AiChatController : ControllerBase
{
    private readonly AiSettingsStore _ai;
    private readonly AiClient _aiClient;
    private readonly ConnectionStore _store;
    private readonly MappingService _mapping;
    private readonly ChatStore _chats;
    private readonly ILogger<AiChatController> _logger;

    public AiChatController(AiSettingsStore ai, AiClient aiClient, ConnectionStore store,
        MappingService mapping, ChatStore chats, ILogger<AiChatController> logger)
    {
        _ai = ai;
        _aiClient = aiClient;
        _store = store;
        _mapping = mapping;
        _chats = chats;
        _logger = logger;
    }

    public record AskRequest(string? SessionId, string Question, bool? Thinking, string? TimeZone,
        string? CurrentQuery);

    /// <summary>会话列表（不含轮次内容）。</summary>
    [HttpGet("/api/ai/chat/{index}")]
    public async Task<IActionResult> Sessions(string index)
    {
        if (!IEsClient.IsValidIndexName(index)) return BadRequest(new { error = $"非法索引名：{index}" });
        var sessions = await _chats.ListAsync(index);
        return Ok(new
        {
            sessions = sessions.Select(s => new { id = s.Id, title = s.Title, updatedAt = s.UpdatedAt, turns = s.Turns.Count })
        });
    }

    /// <summary>单个会话的完整历史。</summary>
    [HttpGet("/api/ai/chat/{index}/{sessionId}")]
    public async Task<IActionResult> Session(string index, string sessionId)
    {
        if (!IEsClient.IsValidIndexName(index)) return BadRequest(new { error = $"非法索引名：{index}" });
        var s = await _chats.GetAsync(index, sessionId);
        if (s == null) return NotFound(new { error = "会话不存在" });
        return Ok(ToClientSession(s));
    }

    [HttpDelete("/api/ai/chat/{index}/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string index, string sessionId)
    {
        if (!IEsClient.IsValidIndexName(index)) return BadRequest(new { error = $"非法索引名：{index}" });
        await _chats.DeleteAsync(index, sessionId);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// 提问（SSE 流式）。事件序列：reasoning* / content*（生成 DSL 阶段）→ dsl{dsl} →
    /// data{表格数据} → reasoning*/content*（总结阶段）→ done{sessionId,title,turn} 或 error{error}。
    /// </summary>
    [HttpPost("/api/ai/chat/{index}")]
    public async Task Ask(string index, [FromBody] AskRequest req, CancellationToken ct)
    {
        if (!_ai.IsConfigured)
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = "尚未配置 AI 服务，请先在「AI 设置」中填写接口地址与密钥" });
            return;
        }
        if (string.IsNullOrWhiteSpace(req.Question))
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = "请输入问题" });
            return;
        }
        if (!IEsClient.IsValidIndexName(index))
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = $"非法索引名：{index}" });
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        var question = req.Question.Trim();
        var sessionId = string.IsNullOrWhiteSpace(req.SessionId)
            ? Guid.NewGuid().ToString("N") : req.SessionId!.Trim();

        try
        {
            var client = _store.Require();
            var meta = await _mapping.GetMetaAsync(index, ct);

            var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            samplingCts.CancelAfter(TimeSpan.FromSeconds(5));
            var samples = await _aiClient.SampleFieldValuesAsync(client, index, meta, samplingCts.Token);

            // 历史上下文：当前会话最近 5 个完整轮次，会话间互不污染。
            // 以最后一个 assistant 为边界成对截断，避免只剩半轮的问答使指代（「再细一点」）失去上下文
            var session = string.IsNullOrWhiteSpace(req.SessionId) ? null : await _chats.GetAsync(index, sessionId);
            var allTurns = session?.Turns ?? [];
            var end = allTurns.FindLastIndex(t => t.Role == "assistant") + 1;
            if (end == 0) end = allTurns.Count; // 无完整问答记录时退化为按条数取
            var start = Math.Max(0, end - 10);
            var historyTurns = allTurns.Skip(start).Take(end - start).ToList();

            var messages = new List<object> { new { role = "system", content = BuildGenSystemPrompt() } };
            foreach (var t in historyTurns)
                messages.Add(new { role = t.Role, content = t.Text });
            messages.Add(new { role = "user", content = BuildGenUserPrompt(index, meta, samples, question, req.TimeZone, req.CurrentQuery) });

            // 第一次生成 DSL
            var content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                (type, text) => WriteSseAsync(type, new { text }, ct), ct);
            var (body, parseError) = ExtractSearchBody(content);

            // 执行；失败把 ES 报错喂回去重新生成一次
            JsonNode? result = null;
            string? execError = null;
            for (var attempt = 0; attempt < 2 && body != null; attempt++)
            {
                if (attempt > 0)
                {
                    await WriteSseAsync("status", new { text = $"查询执行失败（{execError}），正在让 AI 修正重试…" }, ct);
                    messages.Add(new { role = "user", content = $"上一次生成的 DSL 执行失败，错误信息：{execError}。请修正后重新输出，仍只输出一个 JSON 对象。" });
                    content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                        (type, text) => WriteSseAsync(type, new { text }, ct), ct);
                    (body, parseError) = ExtractSearchBody(content);
                }

                await WriteSseAsync("dsl", new { dsl = body!.DeepClone() }, ct);
                try
                {
                    result = await client.SearchAsync(index, (JsonObject)body!.DeepClone(), ct);
                    execError = null;
                    break;
                }
                catch (EsException ex)
                {
                    execError = ex.Message;
                    result = null;
                    _logger.LogWarning("AI 问答 DSL 执行失败（第 {Attempt} 次）：{Index}，{Error}", attempt + 1, index, ex.Message);
                }
            }

            // 两次都没产出可执行 DSL / 仍执行失败：如实回告，不落库历史（避免污染上下文）
            if (body == null || execError != null)
            {
                var msg = execError != null
                    ? $"查询执行失败：{execError}"
                    : $"AI 未能生成有效的查询：{parseError}";
                await WriteSseAsync("error", new { error = msg }, ct);
                return;
            }

            // 表格数据（terms 分布 / 明细行 / 单值聚合），随回答落库以便历史重放
            var tableData = BuildTableData(result!, body!, meta);
            if (tableData != null)
                await WriteSseAsync("data", tableData, ct);

            // 结果喂回模型总结，流式输出
            var resultJson = TrimResult(result!);
            var sumMessages = new List<object>
            {
                new { role = "system", content = BuildSumSystemPrompt() },
                new { role = "user", content = BuildSumUserPrompt(question, resultJson, historyTurns) }
            };
            var answer = await _aiClient.StreamAsync(sumMessages, false,
                (type, text) => type == "content" ? WriteSseAsync("content", new { text }, ct) : Task.CompletedTask, ct);
            if (string.IsNullOrWhiteSpace(answer))
                answer = "（AI 未给出总结，请参考下方查询结果）";

            // 落库：一问一答
            var now = DateTimeOffset.Now;
            var saved = await _chats.AddTurnsAsync(index, sessionId,
            [
                new ChatStore.ChatTurn("user", question, null, null, now),
                new ChatStore.ChatTurn("assistant", answer, body!.ToJsonString(), tableData, now)
            ]);

            await WriteSseAsync("done", new
            {
                sessionId = saved.Id,
                title = saved.Title,
                turn = new { role = "assistant", text = answer, dsl = body.ToJsonString(), data = tableData, time = now }
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // 客户端已中断，静默结束
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await WriteSseAsync("error", new { error = "AI 服务响应超时，请检查接口地址或稍后重试" }, CancellationToken.None);
        }
        catch (EsException ex)
        {
            await WriteSseAsync("error", new { error = ex.Message }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 问答失败：{Index}", index);
            await WriteSseAsync("error", new { error = $"AI 服务调用失败：{ex.Message}" }, ct);
        }
    }

    // ---------- 请求体解析与安全约束 ----------

    private static readonly string[] AllowedRootKeys = ["query", "aggs", "aggregations", "size", "sort", "_source", "track_total_hits"];

    /// <summary>从模型输出提取 search 请求体：剥围栏 → 截 JSON → 白名单键 + 禁 script + 限 size。</summary>
    private static (JsonObject? Body, string? Error) ExtractSearchBody(string content)
    {
        var text = StripFences(content.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return (null, "AI 返回的内容不是有效 JSON");
        JsonNode? node;
        try { node = JsonNode.Parse(text[start..(end + 1)]); }
        catch { return (null, "AI 返回的内容不是有效 JSON"); }
        if (node is not JsonObject obj) return (null, "AI 返回的内容不是 JSON 对象");

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
    /// 递归钳制 terms 聚合的 size（上限 <paramref name="max"/>，下限 1）。
    /// query 的 size 有硬上限，聚合侧此前完全依赖提示词自律；模型在高基数字段上
    /// 给出大 size 的 terms 会让 ES 长时间占用分片缓存，与 size 约束形成不对称保护。
    /// 只改显式写了非法值的节点，不给缺省的 size 赋值，避免无谓地改写模型输出。
    /// </summary>
    private static void ClampTermsSize(JsonNode node, int max = 50)
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

    private static string StripFences(string text)
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

    /// <summary>递归检查是否含 script（含 script_score / script_fields 等一切脚本注入点）。</summary>
    private static bool HasScriptKey(JsonNode node)
    {
        if (node is JsonObject obj)
            return obj.Any(kvp => kvp.Key.EqualsIgnoreCase("script") || kvp.Key.EqualsIgnoreCase("runtime_mappings") || kvp.Key.EqualsIgnoreCase("runtime_fields"))
                || obj.Any(kvp => kvp.Value != null && HasScriptKey(kvp.Value));
        if (node is JsonArray arr)
            return arr.Any(n => n != null && HasScriptKey(n));
        return false;
    }

    // ---------- 结果裁剪与表格数据 ----------

    /// <summary>把执行结果裁剪成喂给模型的紧凑 JSON：聚合桶 ≤20、明细值截断。</summary>
    private static JsonObject TrimResult(JsonNode res)
    {
        var outNode = new JsonObject { ["total"] = res["hits"]?["total"]?["value"]?.DeepClone() ?? 0 };

        if (res["aggregations"] is JsonObject aggs)
        {
            var trimmed = new JsonObject();
            foreach (var (name, agg) in aggs)
            {
                if (agg?["buckets"] is JsonArray buckets)
                {
                    var kept = new JsonArray(buckets.Take(20).Select(b => b!.DeepClone()).ToArray());
                    // terms 分桶可能被 size 截断：ES 原生的 sum_other_doc_count 最准确，
                    // 缺失时退化为「返回桶数超过展示上限」。截断下模型若拿已列桶除以 total 算占比会得出错误结论
                    var other = agg["sum_other_doc_count"] is JsonValue ov && ov.TryGetValue<long>(out var oc) ? oc : 0;
                    if (other > 0)
                        kept.Add(new JsonObject { ["_note"] = $"（terms 分桶不完整：另有 {other:N0} 条文档未纳入以上分桶）" });
                    else if (buckets.Count > 20)
                        kept.Add(new JsonObject { ["_note"] = $"（其余 {buckets.Count - 20} 个桶已省略）" });
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
            foreach (var h in hits.Take(10))
            {
                var row = new JsonObject { ["_id"] = h?["_id"]?.DeepClone() };
                if (h?["_source"] is JsonObject src)
                    foreach (var (k, v) in src)
                    {
                        var s = v?.ToJsonString() ?? "null";
                        if (s.Length > 120) s = s[..120] + "…";
                        row[k] = s;
                    }
                rows.Add(row);
            }
            outNode["rows"] = rows;
        }
        return outNode;
    }

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
    /// 为前端生成可重放的表格数据：terms 分布 → table；数值聚合 → value；明细命中 → table。
    /// terms 分布会带上聚合字段（aggField）与其 nested 路径，前端据此把桶值做成下钻入口。
    /// </summary>
    private static JsonObject? BuildTableData(JsonNode res, JsonObject body, IndexMeta meta)
    {
        var aggs = res["aggregations"] as JsonObject;
        if (aggs != null)
        {
            foreach (var (name, agg) in aggs)
            {
                if (agg?["buckets"] is not JsonArray buckets || buckets.Count == 0) continue;
                var rows = new JsonArray();
                foreach (var b in buckets.Take(20))
                {
                    // 桶值保留原始 JSON 类型（数值字段的 key 是数字），下钻构造 term 时才能对上
                    var key = b?["key"]?.DeepClone() ?? JsonValue.Create("");
                    var docCount = b?["doc_count"]?.GetValue<long>() ?? 0;
                    rows.Add(new JsonArray([key, docCount.ToString("N0")]));
                }
                var table = new JsonObject
                {
                    ["kind"] = "table",
                    ["columns"] = new JsonArray([name, "文档数"]),
                    ["rows"] = rows
                };
                var aggField = FindTermsField(body["aggs"] ?? body["aggregations"], name);
                if (aggField != null)
                {
                    table["aggField"] = aggField;
                    // nested 路径下的字段必须用 nested 查询包裹，否则 term 匹配不到嵌套文档
                    var nested = meta.NestedPaths
                        .Where(p => aggField == p || aggField.StartsWith(p + "."))
                        .OrderByDescending(p => p.Length)
                        .FirstOrDefault();
                    if (nested != null) table["aggNested"] = nested;
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
                    var s = val is JsonObject vo ? vo["value"]?.ToJsonString() : val.ToJsonString();
                    if (!string.IsNullOrWhiteSpace(s)) metricRows.Add(new JsonArray([name, s.Trim('"')]));
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
                var s = src?[c]?.ToJsonString().Trim('"') ?? "";
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

    // ---------- 会话响应 ----------

    private static object ToClientSession(ChatStore.ChatSession s) => new
    {
        id = s.Id,
        title = s.Title,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
        turns = s.Turns.Select(t => new { role = t.Role, text = t.Text, dsl = t.Dsl, data = t.Data, time = t.Time })
    };

    // ---------- Prompt ----------

    private static string BuildGenSystemPrompt() => """
        你是 Elasticsearch 数据分析助手。根据用户问题，生成对目标索引执行 search 的请求体 JSON，用于聚合统计或查询明细。
        规则：
        1. 只输出一个 JSON 对象，禁止任何解释文字、markdown 围栏或注释。
        2. 顶层只能包含：query、aggs、size、sort、_source。禁止 script、suggest、knn、runtime_mappings。
        3. 统计类问题（多少、平均、总和、分布、占比、Top N）：size 设为 0，用 aggs 表达；terms 聚合默认 size 20，最大不超过 50。
        4. 明细类问题（列出、查看、举例）：size 设 1~10，必要时加 sort（如 {"createTime":{"order":"desc"}}），_source 只保留问题相关字段。
        5. 只能使用「字段列表」中给出的字段名（完整路径），禁止编造字段。
        6. 类型选型：keyword/ip/version 用 term（多值用 terms）；text 用 match / match_phrase，需精确匹配时若标注了 keyword 子字段则改用之；数值与日期范围用 range；判断存在用 exists。
        7. 中文时间词转 ES now 表达式（由 ES 执行时计算，禁止换算为绝对日期）：
        """ + AiPrompts.TimeMapping + """
           绝对日期用标准 ISO 格式。
        8. 多条件组合用 bool：且→must，或→should 并加 minimum_should_match:1，非→must_not。
        9. 标注「nested路径」的字段必须用 nested 查询/聚合包裹并指定 path。
        10. 结合上一轮问答历史理解指代（如「再细一点」「换成上个月」）。
        11. 用户消息中若带有「用户当前正在数据浏览中查看的筛选条件」，那是他此刻关注的范围与口径：
            问题未指明范围时沿用它，问题明确指向别的范围时以问题为准，不要被它绑住。
        示例：
        问题：各状态的订单数是多少
        输出：{"query":{"match_all":{}},"size":0,"aggs":{"status_count":{"terms":{"field":"status","size":20}}}}
        问题：上个月订单的平均金额
        输出：{"query":{"range":{"createdAt":{"gte":"now-1M/M","lte":"now/M","time_zone":"+08:00"}}},"size":0,"aggs":{"avg_amount":{"avg":{"field":"amount"}}}}
        问题：金额最高的 5 笔订单，列出状态和金额
        输出：{"query":{"match_all":{}},"size":5,"sort":[{"amount":{"order":"desc"}}],"_source":["status","amount"]}
        """;

    private static string BuildSumSystemPrompt() => """
        你是数据分析师。根据 Elasticsearch 查询结果 JSON，用中文简洁回答用户问题。
        规则：
        1. 所有数字必须来自结果 JSON，禁止编造或自行推算结果中不存在的数据。
        2. 结果为空（total 为 0 或无 buckets/rows）时如实说明。
        3. 先给结论，再给关键数字；控制在 150 字以内。
        4. doc_count 是文档数；percentiles / stats 等指标值原样引用，保留其原有精度，不要自行取整或换算单位。
        5. 用户消息中的「此前用户已提问」仅用于理解本轮问题的指代（如「再细一点」「换成上个月」），只回答本轮问题，不要重复回答历史问题。
        6. buckets 内出现 _note 说明 terms 分桶被 size 截断、数据不完整，此时禁止用已列分桶除以 total 得出占比；被问到占比时如实说明分桶不完整。
        """;

    /// <summary>
    /// 总结阶段的用户消息，附上本轮之前若干条提问作为指代锚点。
    /// 总结阶段不像生成阶段那样能读到完整历史，若只发本轮问句，模型无从解析
    /// 「再细一点」「换成上个月」这类追问，回答会与生成阶段的查询口径脱节。
    /// </summary>
    private static string BuildSumUserPrompt(string question, JsonNode result,
        List<ChatStore.ChatTurn> historyTurns)
    {
        var sb = new System.Text.StringBuilder();
        var prior = historyTurns
            .Where(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text)
            .TakeLast(4)
            .ToList();
        if (prior.Count > 0)
        {
            sb.AppendLine("此前用户已按顺序提问（仅用于理解本轮问题的指代，不要重复作答）：");
            for (var i = 0; i < prior.Count; i++)
                sb.AppendLine($"{i + 1}. {prior[i]}");
            sb.AppendLine();
        }
        sb.Append($"本轮问题：{question}\n\nES 查询结果 JSON：\n{result.ToJsonString()}");
        return sb.ToString();
    }

    private static string BuildGenUserPrompt(string index, IndexMeta meta,
        Dictionary<string, string[]> samples, string question, string? timeZone, string? currentQuery)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"目标索引：{index}（文档数 {meta.DocsCount.ToString("N0")}）");
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            var localNow = DateTimeOffset.UtcNow;
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzi);
            }
            catch { /* 未知时区标识则退回 UTC 时间 */ }
            sb.AppendLine($"当前时间：{localNow:yyyy-MM-dd HH:mm}（时区 {timeZone}）。date 字段的 range 查询请添加 \"time_zone\":\"{timeZone}\"。");
        }
        sb.AppendLine("字段列表（字段名 | 类型[| keyword子字段][| 示例值]）：");
        foreach (var c in meta.Columns)
        {
            var field = c.KeywordPath ?? c.Path;
            var line = $"- {c.Path} | {c.Type}";
            if (c.Type == "text" && c.KeywordPath != null) line += $" | keyword子字段: {c.KeywordPath}";
            var nestedPath = meta.NestedPaths
                .Where(p => c.Path == p || c.Path.StartsWith(p + "."))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();
            if (nestedPath != null) line += $" | nested路径: {nestedPath}";
            if (samples.TryGetValue(field, out var vals))
                line += $" | 示例值: {string.Join(", ", vals)}";
            sb.AppendLine(line);
        }
        if (!string.IsNullOrWhiteSpace(currentQuery))
        {
            // 浏览上下文：说明它是「参考」而非「要复现的目标」，否则模型容易直接照抄这份筛选条件，
            // 导致用户问别的口径时仍被历史条件绑住
            sb.AppendLine($"用户当前正在「数据浏览」中查看以下筛选条件（仅供理解他关注的范围与口径，不要机械照搬；本轮问题优先）：");
            sb.AppendLine(currentQuery);
        }
        sb.AppendLine();
        sb.Append($"用户问题：{question}");
        return sb.ToString();
    }

    private async Task WriteSseAsync(string type, object payload, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        node["type"] = type;
        await Response.WriteAsync($"data: {node.ToJsonString()}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}

file static class JsonNodeKeyExtensions
{
    public static bool EqualsIgnoreCase(this string key, string other) =>
        string.Equals(key, other, StringComparison.OrdinalIgnoreCase);
}
