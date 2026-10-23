using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace EsClient.Api.Services;

/// <summary>
/// AI 访问门面：对 Controller 保持原有 StreamAsync 契约（SSE 双事件回调 + 完整 content），
/// 内部改走 MEAI 的 IChatClient 管道 —— 自研 OpenAiCompatibleChatClient（HTTP/SSE 解析）
/// + JsonModeFallbackChatClient（response_format 降级重试中间件）。
/// 端点配置支持运行时热切换：每次调用比对配置快照，变了就重建客户端链。
/// 字段值采样（SampleFieldValuesAsync）与 AI 传输无关，留在本类。
/// </summary>
public class AiClient(IHttpClientFactory httpFactory, AiSettingsStore ai, ILoggerFactory loggerFactory)
{
    public bool IsConfigured => ai.IsConfigured;

    /// <summary>缓存的客户端链与其配置快照键（BaseUrl/ApiKey/Model/ThinkingModel/JsonMode 任一变化即失效）。</summary>
    private IChatClient? _chain;
    private string? _chainKey;
    private readonly object _chainLock = new();

    /// <summary>
    /// 以 stream 模式调用上游接口，逐块回调 onEvent("reasoning"/"content", text)，返回完整 content。
    /// thinking 为 true 时优先使用配置的思考模型。
    /// jsonOutput 为 true 表示本次要求模型输出 JSON，请求会带 response_format=json_object：
    /// 能显著降低围栏/语法错误导致的解析失败，但端点支持度不统一，故不支持时由降级中间件
    /// 自动重试一次（结论记入能力档案）。总结、直接回答这类自由文本调用不要开。
    /// </summary>
    /// <param name="tools">只读工具集（<see cref="AiTools"/> 构建）。非空时启用分步取数回路，
    /// 模型可多次调用工具后再产出最终 DSL；回调经 onEvent 以 "status" 事件透出工具执行进度。</param>
    public async Task<string> StreamAsync(List<ChatMessage> messages, bool thinking,
        Func<string, string, Task>? onEvent, CancellationToken ct, bool jsonOutput = false,
        IList<AITool>? tools = null)
    {
        var s = ai.Get();
        var model = thinking && !string.IsNullOrWhiteSpace(s.ThinkingModel) ? s.ThinkingModel : s.Model;

        var options = new ChatOptions { ModelId = model, Temperature = 0.2f };
        // 本次是否带 response_format=json_object。三条约束：
        // ① 只有要求输出 JSON 的调用才开（总结/直接回答是自由文本，开了反而被约束成对象）；
        // ② 思考模式一律不开——部分推理模型（R1、qwen-thinking）在 JSON 模式下可能返回空 content；
        // ③ 端点已确认不支持则跳过。
        // ④ 带工具时不开——工具轮需自由发挥，json_object 会与之冲突（部分端点直接报错）。
        if (tools is { Count: > 0 })
        {
            options.Tools = tools;
            // 工具执行进度经 onEvent("status", …) 透出（前端 status 事件已有渲染，零协议改动）
            (options.AdditionalProperties ??= [])[ToolLoopChatClient.StatusCallbackKey] =
                new Func<string, Task>(t => onEvent?.Invoke(SseEvent.Status, t) ?? Task.CompletedTask);
        }
        else if (UseJsonMode(s, model, thinking, jsonOutput))
            (options.AdditionalProperties ??= [])[JsonModeCapability.ResponseFormatKey] = "json_object";

        // 读流总时长自行控制：HTTP 客户端超时设为无限，这里 4 分钟兜底（客户端主动取消另有 HandleErrorAsync 静默处理）
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        streamCts.CancelAfter(TimeSpan.FromMinutes(4));

        var contentBuilder = new StringBuilder();
        var client = GetChain(s);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, streamCts.Token))
        {
            foreach (var item in update.Contents)
            {
                switch (item)
                {
                    case TextReasoningContent r when r.Text.Length > 0 && onEvent != null:
                        await onEvent(SseEvent.Reasoning, r.Text);
                        break;
                    case TextContent t when t.Text.Length > 0:
                        contentBuilder.Append(t.Text);
                        if (onEvent != null) await onEvent(SseEvent.Content, t.Text);
                        break;
                }
            }
        }
        return contentBuilder.ToString();
    }

    /// <summary>取当前配置对应的客户端链；配置快照变化（保存/切换模型）时重建。</summary>
    private IChatClient GetChain(AiModelConfig s)
    {
        var key = $"{s.BaseUrl}|{s.ApiKey}|{s.Model}|{s.ThinkingModel}|{s.JsonMode}";
        lock (_chainLock)
        {
            if (_chainKey != key || _chain == null)
            {
                // 由内到外：connector（HTTP/SSE）→ 降级中间件 → 工具回路（最外层，产出 status 事件并重发）
                _chain = new ToolLoopChatClient(
                    new JsonModeFallbackChatClient(
                        new OpenAiCompatibleChatClient(httpFactory, s.BaseUrl, s.ApiKey),
                        s.JsonMode == true, s.BaseUrl),
                    loggerFactory.CreateLogger<ToolLoopChatClient>());
                _chainKey = key;
            }
            return _chain;
        }
    }

    /// <summary>本次请求是否带 response_format=json_object，判定规则见 StreamAsync 内注释。</summary>
    private static bool UseJsonMode(AiModelConfig s, string model, bool thinking, bool jsonOutput)
    {
        if (!jsonOutput || thinking || s.JsonMode == false) return false;
        return s.JsonMode == true || !JsonModeCapability.Rejected.ContainsKey(JsonModeCapability.EndpointKey(s.BaseUrl, model));
    }

    /// <summary>
    /// 对可精确匹配的字段做 terms/stats 聚合采样，供 AI 选择真实存在的字段与值。
    /// 与 AI 传输无关（纯 ES 查询），因属 AI 链路的辅助步骤而留在此类。
    /// </summary>
    public async Task<Dictionary<string, string[]>> SampleFieldValuesAsync(
        EsHttpClient client, string index, IndexMeta meta, CancellationToken ct)
    {
        try
        {
            // nested 路径下的字段不能在本层聚合里采：根级 terms/stats 看不到嵌套文档的值，
            // 采出来必然是空，白白占用下方有限的名额，故直接排除（其示例值由 nested 聚合另行提供）
            bool IsNested(ColumnMeta c) => meta.IsNested(c.Path);

            var fields = meta.Columns
                .Where(c => !IsNested(c) && (c.Type is "keyword" or "ip" or "version" ||
                            (c.Type == "text" && c.KeywordPath != null)))
                .Select(c => c.KeywordPath ?? c.Path)
                .Distinct()
                .Take(12)
                .ToList();
            var numeric = meta.Columns
                .Where(c => !IsNested(c) && ColumnMeta.NumericTypes.Contains(c.Type))
                .Select(c => c.Path)
                .Take(4);
            fields.AddRange(numeric.Except(fields));
            var dates = meta.Columns
                .Where(c => !IsNested(c) && ColumnMeta.DateTypes.Contains(c.Type))
                .Select(c => c.Path)
                .Take(3);
            fields.AddRange(dates.Except(fields));
            if (fields.Count == 0) return new Dictionary<string, string[]>();

            var dateSet = dates.ToHashSet();
            var aggs = new JsonObject();
            foreach (var f in fields)
                aggs[$"a{aggs.Count}"] = dateSet.Contains(f)
                    ? new JsonObject { ["stats"] = new JsonObject { ["field"] = f } }
                    : new JsonObject { ["terms"] = new JsonObject { ["field"] = f, ["size"] = 5 } };

            var res = await client.SearchAsync(index,
                new JsonObject { ["size"] = 0, ["aggs"] = aggs }, ct);

            var dict = new Dictionary<string, string[]>();
            var i = 0;
            foreach (var f in fields)
            {
                if (dateSet.Contains(f))
                {
                    var stats = res?["aggregations"]?[$"a{i}"] as JsonObject;
                    var min = EpochToIso(stats?["min"]);
                    var max = EpochToIso(stats?["max"]);
                    if (min != null && max != null) dict[f] = [$"{min} ~ {max}"];
                }
                else
                {
                    var buckets = res?["aggregations"]?[$"a{i}"]?["buckets"] as JsonArray;
                    var vals = buckets?.Select(b => b?["key"])
                        .Select(k => k is JsonValue v
                            ? v.TryGetValue<string>(out var s) ? s : k?.ToJsonString().Trim('"') ?? ""
                            : k?.ToJsonString() ?? "")
                        .Where(s => s.Length > 0)
                        .Take(5)
                        .ToArray() ?? [];
                    if (vals.Length > 0) dict[f] = vals;
                }
                i++;
            }
            return dict;
        }
        catch
        {
            return new Dictionary<string, string[]>(); // 采样失败不阻塞主流程
        }
    }

    /// <summary>把 ES stats 聚合返回的时间数值（epoch 毫秒/纳秒）转为 ISO 字符串。</summary>
    private static string? EpochToIso(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        double n;
        try
        {
            if (v.TryGetValue<double>(out var d)) n = d;
            else if (v.TryGetValue<long>(out var l)) n = l;
            else return null;
        }
        catch { return null; }
        if (double.IsNaN(n) || n <= 0) return null;
        var ms = n > 1e14 ? n / 1e6 : n; // 纳秒 → 毫秒
        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    /// <summary>安全读取 JsonNode 字符串（缺失 / JSON null / 非字符串均返回 null）。供 connector 解析 SSE 使用。</summary>
    public static string? NodeStr(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>从上游错误响应体里提取 OpenAI 格式的 error.message，供错误文案与降级判定使用。</summary>
    public static string? TryExtractOpenAiError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?["message"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
