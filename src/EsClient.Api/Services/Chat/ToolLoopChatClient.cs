using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Services;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace EsClient.Api.Services.Chat;

/// <summary>
/// 只读工具回路中间件：模型返回 FunctionCallContent 时逐个执行 AIFunction、把结果回填
/// 消息历史并重新发起，直到模型不再调工具或触达轮次上限。
/// 不用 MEAI 自带的 FunctionInvokingChatClient，原因有二：
/// ① 9.5 版其函数解析辅助（GetFunctionRequestInstruction 等）不在公共面，自研免疫 API 变动；
/// ② 我们需要在「工具执行前后」直接产出 status 事件给前端（本地工具无确认环节），
///   官方实现的事件通道走 ILogger，与本项目的 SSE 协议对不上。
/// 循环全部在内存态消息上进行，ChatStore 落库格式不受影响。
/// </summary>
public sealed class ToolLoopChatClient(IChatClient inner, ILogger logger, int maxIterations = 4) : DelegatingChatClient(inner)
{
    /// <summary>ChatOptions.AdditionalProperties 保留键：工具执行提示回调 Func&lt;string,Task&gt;（门面注入，可空）。</summary>
    public const string StatusCallbackKey = "esclient.tool_status";

    /// <summary>ChatOptions.AdditionalProperties 保留键：工具步骤收集回调 Action&lt;ToolStep&gt;（门面注入，可空）。
    /// 与 status 文本事件并存——status 供流式实时展示，本收集供整轮结束后随问答落库、历史重放成折叠摘要。</summary>
    public const string ToolStepsKey = "esclient.tool_steps";

    /// <summary>单次工具调用的结构化留痕：展示名、参数预览、结果预览、是否成功、耗时。字符串已在收集处截断。</summary>
    public sealed record ToolStep(string Name, string Display, string Args, string Result, bool Ok, long ElapsedMs);

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken ct = default)
    {
        var msgs = messages.ToList();
        for (var round = 0; ; round++)
        {
            var res = await base.GetResponseAsync(msgs, options, ct);
            var responseMsg = res.Messages.FirstOrDefault();
            var calls = responseMsg?.Contents.OfType<FunctionCallContent>().ToList();
            if (calls is not { Count: > 0 }) return res;
            if (round >= maxIterations) return res; // 上限触达：把带工具调用的最后响应交给上层，不再续问
            msgs.Add(responseMsg!);
            msgs.Add(await InvokeAllAsync(calls, options, ct));
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgs = messages.ToList();

        // 无工具 → 纯透传（保留逐块 reasoning 流式输出，不物化）
        if (options?.Tools is not { Count: > 0 })
        {
            await foreach (var update in base.GetStreamingResponseAsync(msgs, options, ct)
                .WithCancellation(ct).ConfigureAwait(false))
                yield return update;
            yield break;
        }

        // 有工具 → 逐轮：思考过程/正文即时透传（不物化，保留流式体验），仅缓存 FunctionCallContent
        // 待轮末执行——工具调用分片只有流完才能拼出完整 arguments，无法增量下发，但这部分本就
        // 不该展示给用户。中间轮的思考（模型据此决定调哪个工具）与最终轮正文都能实时看到。
        while (true)
        {
            var calls = new List<FunctionCallContent>();
            var textSb = new System.Text.StringBuilder();
            await foreach (var update in base.GetStreamingResponseAsync(msgs, options, ct)
                .WithCancellation(ct).ConfigureAwait(false))
            {
                var passthrough = new List<AIContent>();
                foreach (var item in update.Contents)
                {
                    if (item is FunctionCallContent c) { calls.Add(c); continue; }
                    if (item is TextContent t) textSb.Append(t.Text);
                    passthrough.Add(item);
                }
                if (passthrough.Count > 0)
                    yield return new ChatResponseUpdate(ChatRole.Assistant, passthrough);
            }

            if (calls.Count == 0) yield break; // 最终轮：思考与正文已即时流出

            var roundContents = new List<AIContent>();
            if (textSb.Length > 0) roundContents.Add(new TextContent(textSb.ToString()));
            roundContents.AddRange(calls);
            msgs.Add(new ChatMessage(ChatRole.Assistant, roundContents));
            msgs.Add(await InvokeAllAsync(calls, options, ct));
        }
    }

    /// <summary>串行执行一轮内的全部工具调用（parallel_tool_calls 已关，正常只有一个）。
    /// 单个工具抛错不终止回路：错误文本回填给模型自愈，与 DSL 执行失败喂回重试同思路。</summary>
    private async Task<ChatMessage> InvokeAllAsync(List<FunctionCallContent> calls,
        ChatOptions? options, CancellationToken ct)
    {
        Func<string, Task>? status = null;
        Action<ToolStep>? collect = null;
        if (options?.AdditionalProperties != null)
        {
            if (options.AdditionalProperties.TryGetValue(StatusCallbackKey, out var cb) && cb is Func<string, Task> act)
                status = act;
            if (options.AdditionalProperties.TryGetValue(ToolStepsKey, out var scb) && scb is Action<ToolStep> act2)
                collect = act2;
        }

        var msg = new ChatMessage(ChatRole.Assistant, new List<AIContent>());
        foreach (var call in calls)
        {
            var fn = options?.Tools?.OfType<AIFunction>()
                .FirstOrDefault(f => string.Equals(f.Name, call.Name, StringComparison.Ordinal));

            var argsPreview = FormatArgs(call.Arguments);
            // 展示/日志用迭代解包内嵌 JSON（模型双重编码、源数据自带转义都剥掉）；日志里 {Args} 走模板不转义，status 文本需干净
            var argsDisp = JsonDisplayUnwrapper.Unwrap(argsPreview);
            if (status != null)
                await status(fn is null
                    ? $"模型请求了未知工具 {call.Name}"
                    : $"🔧 {AiTools.DisplayName(call.Name)} ← {Truncate(argsDisp, 120)}");

            logger.LogInformation("工具调用 {Tool} args={Args}", call.Name, argsDisp);

            object? result;
            Exception? error = null;
            var sw = Stopwatch.StartNew();
            if (fn == null)
            {
                error = new InvalidOperationException($"未知工具：{call.Name}");
                result = null;
            }
            else
            {
                try { result = await fn.InvokeAsync(new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>()), ct); }
                catch (Exception ex) { error = ex; result = null; }
            }
            sw.Stop();

            var resultStr = result?.ToString() ?? "";
            // 预览用解包后文本（否则 status 整段是字符串，传输再转义一层、前端原样显示 \"）；
            // 回填给模型的 FunctionResultContent 仍用原样合法 JSON
            var previewStr = JsonDisplayUnwrapper.Unwrap(resultStr);
            if (error != null)
                logger.LogWarning(error, "工具 {Tool} 执行失败（{ElapsedMs}ms）", call.Name, sw.ElapsedMilliseconds);
            else
                logger.LogInformation("工具 {Tool} 完成（{ElapsedMs}ms）→ {Result}", call.Name, sw.ElapsedMilliseconds, Truncate(previewStr, 500));

            if (error != null && status != null)
                await status($"🔧 {AiTools.DisplayName(call.Name)} ✗ {Truncate(error.Message, 80)}");
            else if (status != null)
                await status($"🔧 {AiTools.DisplayName(call.Name)} → {Truncate(previewStr, 200)}");

            // 留痕落库（可空收集）：参数/结果按展示口径截断，控制会话文件体积
            collect?.Invoke(new ToolStep(call.Name, AiTools.DisplayName(call.Name),
                Truncate(argsDisp, 200),
                error != null ? Truncate(error.Message, 200) : Truncate(previewStr, 400),
                error == null, sw.ElapsedMilliseconds));

            msg.Contents.Add(new FunctionResultContent(call.CallId ?? "", error != null ? null : result) { Exception = error });
        }
        return msg;
    }

    /// <summary>参数预览：值为 JSON 字符串的（如 dsl/query）解析后内嵌，避免二次转义成 {\"query\":...}。</summary>
    private static string FormatArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return "{}";
        var obj = new JsonObject();
        foreach (var (k, v) in args)
        {
            // 字符串值且形如对象/数组 → 当作 JSON 解析内嵌（解析失败则原样保留字符串）
            if (v is string s && (s.StartsWith('{') || s.StartsWith('[')))
            {
                try { obj[k] = JsonNode.Parse(s); continue; } catch { /* 非法 JSON，按普通字符串 */ }
            }
            obj[k] = v switch
            {
                null => null,
                string str => JsonValue.Create(str),
                var o => JsonSerializer.SerializeToNode(o)
            };
        }
        return obj.ToJsonString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
