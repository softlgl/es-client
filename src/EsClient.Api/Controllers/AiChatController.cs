using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

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
        string? CurrentQuery, bool? Tools);

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
    /// 与索引数据无关的问题（常识/观点/闲聊）在生成阶段以 {"no_query":true} 免查询，
    /// 事件序列为 content* → direct{} → content*（直接回答）→ done，不产生 dsl/data。
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
            // 以最后一个 assistant 为边界成对截断（见 HistoryWindow），避免只剩半轮的问答使指代（「再细一点」）失去上下文
            var session = string.IsNullOrWhiteSpace(req.SessionId) ? null : await _chats.GetAsync(index, sessionId);
            var allTurns = session?.Turns ?? [];
            var historyTurns = HistoryWindow.Take(allTurns, 5, t => t.Role);

            var messages = new List<ChatMessage> { new(ChatRole.System, AiPrompts.ChatGenerateSystem(req.Tools == true)) };
            foreach (var t in historyTurns)
                messages.Add(new ChatMessage(new ChatRole(t.Role), t.Text));
            messages.Add(new ChatMessage(ChatRole.User,
                AiPrompts.ChatGenerateUser(index, meta, samples, question, req.TimeZone, req.CurrentQuery)));

            // 分步取数回路：开关打开时下发只读工具集，模型可先跑临时查询/采样验证口径再产出最终 DSL。
            // 工具均为后端只读操作，循环在服务端闭环完成，不需前端确认；写入类能力根本不注册。
            var tools = req.Tools == true ? AiTools.Build(client, index, meta, ct) : null;

            // 第一次生成 DSL
            var content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                (type, text) => WriteSseAsync(type, new { text }, ct), ct, jsonOutput: true, tools);
            var (body, parseError) = AiDslGuard.ExtractSearchBody(content);

            // 常识/观点/闲聊等与索引数据无关的问题：模型标记免查询，直接回答。
            // 不走查数链路——否则既浪费一次 ES 查询，回答还会被总结提示词
            // （数字只能来自结果 JSON）绑死，观点类问题无法作答
            if (body == null && AiDslGuard.IsNoQueryMarker(content))
            {
                // 通知前端撤掉 DSL 预览，后续 content 转进回答气泡
                await WriteSseAsync(SseEvent.Direct, new { }, ct);
                var directMessages = new List<ChatMessage> { new(ChatRole.System, AiPrompts.ChatDirectSystem()) };
                foreach (var t in historyTurns)
                    directMessages.Add(new ChatMessage(new ChatRole(t.Role), t.Text));
                directMessages.Add(new ChatMessage(ChatRole.User, question));
                var directAnswer = await _aiClient.StreamAsync(directMessages, false,
                    (type, text) => type == SseEvent.Content ? WriteSseAsync(SseEvent.Content, new { text }, ct) : Task.CompletedTask, ct);
                if (string.IsNullOrWhiteSpace(directAnswer))
                    directAnswer = "（AI 未给出回答）";

                var directNow = DateTimeOffset.Now;
                var directSaved = await _chats.AddTurnsAsync(index, sessionId,
                [
                    new ChatStore.ChatTurn("user", question, null, null, directNow),
                    new ChatStore.ChatTurn("assistant", directAnswer, null, null, directNow)
                ]);
                await WriteSseAsync(SseEvent.Done, new
                {
                    sessionId = directSaved.Id,
                    title = directSaved.Title,
                    turn = new { role = "assistant", text = directAnswer, dsl = (string?)null, data = (JsonNode?)null, time = directNow }
                }, ct);
                return;
            }

            // 执行；失败把 ES 报错喂回去重新生成一次
            JsonNode? result = null;
            string? execError = null;
            for (var attempt = 0; attempt < 2 && body != null; attempt++)
            {
                if (attempt > 0)
                {
                    await WriteSseAsync(SseEvent.Status, new { text = $"查询执行失败（{execError}），正在让 AI 修正重试…" }, ct);
                    messages.Add(new ChatMessage(ChatRole.User,
                        $"上一次生成的 DSL 执行失败，错误信息：{execError}。请修正后重新输出，仍只输出一个 JSON 对象。"));
                    content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                        (type, text) => WriteSseAsync(type, new { text }, ct), ct, jsonOutput: true, tools);
                    (body, parseError) = AiDslGuard.ExtractSearchBody(content);
                    if (body == null) break; // 修正后仍解析不出可执行 DSL，交给下方收尾统一回告，避免解引用 null
                }

                await WriteSseAsync(SseEvent.Dsl, new { dsl = body!.DeepClone() }, ct);
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
                var msg = body == null
                    ? $"AI 未能生成有效的查询：{parseError}"
                    : $"查询执行失败：{execError}";
                await WriteSseAsync(SseEvent.Error, new { error = msg }, ct);
                return;
            }

            // 表格数据（terms 分布 / 明细行 / 单值聚合），随回答落库以便历史重放
            var tableData = AiResultProjector.BuildTableData(result!, body!, meta);
            if (tableData != null)
                await WriteSseAsync(SseEvent.Data, tableData, ct);

            // 结果喂回模型总结，流式输出
            var resultJson = AiResultProjector.TrimForModel(result!, bucketCap: 20, rowCap: 10, cellMax: 120);
            var sumMessages = new List<ChatMessage>
            {
                new(ChatRole.System, AiPrompts.ChatSummarizeSystem()),
                new(ChatRole.User, AiPrompts.ChatSummarizeUser(question, resultJson, historyTurns))
            };
            var answer = await _aiClient.StreamAsync(sumMessages, false,
                (type, text) => type == SseEvent.Content ? WriteSseAsync(SseEvent.Content, new { text }, ct) : Task.CompletedTask, ct);
            if (string.IsNullOrWhiteSpace(answer))
                answer = "（AI 未给出总结，请参考下方查询结果）";

            // 落库：一问一答
            var now = DateTimeOffset.Now;
            var saved = await _chats.AddTurnsAsync(index, sessionId,
            [
                new ChatStore.ChatTurn("user", question, null, null, now),
                new ChatStore.ChatTurn("assistant", answer, body!.ToJsonString(), tableData, now)
            ]);

            await WriteSseAsync(SseEvent.Done, new
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


