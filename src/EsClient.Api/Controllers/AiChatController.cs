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

        SseStream.Begin(Response);

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

            var messages = new List<object> { new { role = "system", content = AiPrompts.ChatGenerateSystem() } };
            foreach (var t in historyTurns)
                messages.Add(new { role = t.Role, content = t.Text });
            messages.Add(new { role = "user", content = AiPrompts.ChatGenerateUser(index, meta, samples, question, req.TimeZone, req.CurrentQuery) });

            // 第一次生成 DSL
            var content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                (type, text) => WriteSseAsync(type, new { text }, ct), ct);
            var (body, parseError) = AiDslGuard.ExtractSearchBody(content);

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
                    (body, parseError) = AiDslGuard.ExtractSearchBody(content);
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
                new { role = "system", content = AiPrompts.ChatSummarizeSystem() },
                new { role = "user", content = AiPrompts.ChatSummarizeUser(question, resultJson, historyTurns) }
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
        catch (Exception ex)
        {
            await SseStream.HandleErrorAsync(Response, ex, _logger, $"AI 问答失败：{index}", ct);
        }
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
                    var nested = meta.ResolveNestedPath(aggField);
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

    

    private Task WriteSseAsync(string type, object payload, CancellationToken ct) =>
        SseStream.WriteAsync(Response, type, payload, ct);
}


