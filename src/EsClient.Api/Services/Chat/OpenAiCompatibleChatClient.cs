using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using Microsoft.Extensions.AI;

namespace EsClient.Api.Services.Chat;

/// <summary>
/// 上游 HTTP 非 2xx 的结构化异常：带原始状态码与提取后的错误详情。
/// connector 只负责如实抛出，「能否降级」的判断留给中间件、「翻译成用户文案」留给门面，
/// 三层各管一段 —— 这是裸 HTTP 手写时代把 status/detail 干净透出管道的方式。
/// </summary>
public sealed class UpstreamHttpException(int status, string detail) : Exception($"AI 服务返回错误（{status}）：{detail}")
{
    public int Status { get; } = status;
    public string Detail { get; } = detail;
}

/// <summary>
/// MEAI IChatClient 的自研 OpenAI 兼容端点实现（方案 B：借 MEAI 的接口标准与中间件管道，
/// 不借它的 HTTP 实现）。HTTP + SSE 解析沿用原 AiClient.StreamAsync 里打磨过的逻辑：
/// qwen 空 choices chunk 跳过、reasoning_content/reasoning 双字段名、"content": null 安全取值，
/// 这些针对国产端点的兼容细节官方 connector 覆盖不到，留在自己手里才可控。
/// 一个实例绑定一套端点配置，配置热切换由门面的工厂负责重建。
/// </summary>
public sealed class OpenAiCompatibleChatClient(IHttpClientFactory httpFactory, string baseUrl, string apiKey) : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("openai-compatible", new Uri(baseUrl), null);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken ct = default) => CollectAsync(messages, options, ct);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var msgList = messages as IList<ChatMessage> ?? messages.ToList();
        var http = httpFactory.CreateClient("ai"); // 无限超时由 Program.cs 的命名注册一次性设定，读流时长交门面 CTS

        using var reqMsg = BuildRequest(msgList, options);
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new EsException($"无法连接 AI 服务（{baseUrl}）：{ex.Message}", 502);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EsException("AI 服务响应超时，请检查接口地址或稍后重试", 504);
        }

        using (res)
        {
            if (!res.IsSuccessStatusCode)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                var detail = AiClient.TryExtractOpenAiError(text) ?? text;
                throw new UpstreamHttpException((int)res.StatusCode, detail);
            }

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var toolCalls = new OpenAiWireFormat.ToolCallAggregator();

            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                JsonNode? chunk;
                try { chunk = JsonNode.Parse(data); } catch { continue; }

                // qwen 等会先发一个 choices 为空数组的 chunk，需跳过
                var choices = chunk?["choices"] as JsonArray;
                var delta = choices is { Count: > 0 } ? choices[0]?["delta"] : null;
                if (delta == null) continue;

                // 推理模型（DeepSeek-R1 等）的思考过程字段；qwen 会显式发 "content": null，须安全取值
                var reasoning = AiClient.NodeStr(delta["reasoning_content"]) ?? AiClient.NodeStr(delta["reasoning"]);
                var content = AiClient.NodeStr(delta["content"]);

                // 工具调用分片先进归并器，流结束后统一产出（arguments 跨块拼接，中途无法解析）
                if (delta["tool_calls"] is JsonArray tcDeltas) toolCalls.Feed(tcDeltas);

                if (string.IsNullOrEmpty(reasoning) && string.IsNullOrEmpty(content)) continue;

                // 逐 delta 即时产出，事件粒度与原 onEvent 回调一致，前端无感知
                var update = new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>());
                if (!string.IsNullOrEmpty(reasoning)) update.Contents.Add(new TextReasoningContent(reasoning));
                if (!string.IsNullOrEmpty(content)) update.Contents.Add(new TextContent(content));
                yield return update;
            }

            // 模型决定调工具：把归并结果以 MEAI 标准类型产出，循环交给上层 FunctionInvoking 中间件
            if (toolCalls.HasCalls)
                yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>(toolCalls.BuildCalls()));
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    /// <summary>非流式消费：本项目门面只走流式，此方法仅为满足接口完整性。</summary>
    private async Task<ChatResponse> CollectAsync(IEnumerable<ChatMessage> messages, ChatOptions? options,
        CancellationToken ct)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, ct)) updates.Add(u);
        return updates.ToChatResponse();
    }

    private HttpRequestMessage BuildRequest(IList<ChatMessage> messages, ChatOptions? options)
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
        reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? "" : options.ModelId,
            ["messages"] = OpenAiWireFormat.MessagesArray(messages),
            ["temperature"] = options?.Temperature ?? 0.2,
            ["stream"] = true
        };
        // 工具下发：有工具时端点可自主决定调用；并行工具调用在国产端点质量参差，显式关闭
        if (options?.Tools is { Count: > 0 } tools)
        {
            body["tools"] = OpenAiWireFormat.ToolsArray(tools);
            body["parallel_tool_calls"] = false;
        }
        // OpenAI 系还要求 messages 里出现 "json" 字样才允许该参数，本项目的生成类提示词均已包含
        if (options?.AdditionalProperties != null &&
            options.AdditionalProperties.TryGetValue(JsonModeCapability.ResponseFormatKey, out var rf) &&
            rf is not null)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        reqMsg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return reqMsg;
    }
}
