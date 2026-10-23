using System.Runtime.CompilerServices;
using EsClient.Api.Es;
using Microsoft.Extensions.AI;

namespace EsClient.Api.Services.Chat;

/// <summary>
/// JSON 模式降级中间件：带 response_format 的请求被端点拒绝时，记入能力档案并去掉该参数
/// 重发一次（原 AiClient 发送循环里的 while(true) 重试逻辑，如今有了安身之处）。
/// 用户显式「强制开启」JSON 模式时不擅自降级：让错误直接暴露成 EsException 到界面，由用户决定。
/// 能力档案进程级共享，见 <see cref="JsonModeCapability"/>。
/// </summary>
public sealed class JsonModeFallbackChatClient(IChatClient inner, bool jsonModeForced, string baseUrl)
    : DelegatingChatClient(inner)
{
    private const int DetailCap = 200; // 错误详情进用户可见文案前的截断长度

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, ct);
        }
        catch (UpstreamHttpException ex) when (ShouldFallback(ex, options))
        {
            Reject(options);
            return await base.GetResponseAsync(messages, options, ct);
        }
        catch (UpstreamHttpException ex)
        {
            throw ToEsException(ex);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 迭代器惰性转发，上游异常在枚举阶段由 RetryOnJsonRejectAsync 统一处理
        await foreach (var update in RetryOnJsonRejectAsync(messages, options, ct).WithCancellation(ct))
            yield return update;
    }

    /// <summary>
    /// 带降级重试的流式拉取。两点约束：
    /// ① 迭代器首次 MoveNext 才真正发请求，异常在那里而非取枚举器那一步抛出，故先手动探一步；
    /// ② 降级重发只允许发生在「尚未向下游产出任何增量」时（response_format 的拒绝在响应头阶段），
    ///   一旦已吐过内容就照常往外抛，否则前端会看到重复输出。
    /// C# 禁止在带 catch 的 try 内 yield，故探测阶段只缓存首个增量、确认无异常后才产出。
    /// </summary>
    private async IAsyncEnumerable<ChatResponseUpdate> RetryOnJsonRejectAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var e = base.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
        ChatResponseUpdate? pending = null;
        var hasPending = false;
        UpstreamHttpException? rejected = null;
        try
        {
            // 只探一步：失败时尚未向下游产出任何内容，可安全降级重发（try 带 catch，不能在此 yield）
            if (await e.MoveNextAsync()) { pending = e.Current; hasPending = true; }
        }
        catch (UpstreamHttpException ex) when (!hasPending && ShouldFallback(ex, options))
        {
            rejected = ex;
        }
        if (rejected == null)
        {
            try
            {
                if (hasPending) yield return pending!;
                while (await e.MoveNextAsync()) yield return e.Current;
            }
            finally { await e.DisposeAsync(); }
            yield break;
        }
        await e.DisposeAsync();

        Reject(options);
        // 降级后重新发起整个调用。同样受限于「catch 的 try 内不能 yield」，手动物化转发；
        // 仍被拒（或其他上游错误）不再探测，直接翻译成用户可见文案，与改造前一致
        await using var e2 = base.GetStreamingResponseAsync(messages, options, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            bool ok;
            try { ok = await e2.MoveNextAsync(); }
            catch (UpstreamHttpException ex) { throw ToEsException(ex); }
            if (!ok) break;
            yield return e2.Current;
        }
    }

    /// <summary>
    /// 是否值得降级重试一次：本次带了 response_format、非用户强制、且 4xx 文案像是指向该参数。
    /// </summary>
    private bool ShouldFallback(UpstreamHttpException ex, ChatOptions? options) =>
        options != null &&
        options.AdditionalProperties?.ContainsKey(JsonModeCapability.ResponseFormatKey) == true &&
        jsonModeForced != true &&
        JsonModeCapability.LooksLikeRejected(ex.Status, ex.Detail);

    private void Reject(ChatOptions? options)
    {
        JsonModeCapability.Rejected[JsonModeCapability.EndpointKey(baseUrl, options!.ModelId ?? "")] = true;
        options.AdditionalProperties!.Remove(JsonModeCapability.ResponseFormatKey);
    }

    /// <summary>不可降级的上游错误 → 用户可见的 EsException（文案与原实现一致）。</summary>
    private static EsException ToEsException(UpstreamHttpException ex) =>
        new($"AI 服务返回错误（{ex.Status}）：{Truncate(ex.Detail)}", 502);

    private static string Truncate(string s) => s[..Math.Min(s.Length, DetailCap)];
}
