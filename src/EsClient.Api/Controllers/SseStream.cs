using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services;

namespace EsClient.Api.Controllers;

/// <summary>
/// SSE 响应写出与异常兜底。
/// 「AI 生成查询」与「AI 问答」的流式响应结构完全一致，此前各自复制了一份
/// 写出方法与四段 catch 链：服务端新增事件类型或调整文案时必须同步改两处，
/// 漏改的后果是两条链路的错误提示悄悄分叉。
/// </summary>
internal static class SseStream
{
    /// <summary>设置流式响应头。必须在第一次写之前调用。</summary>
    public static void Begin(HttpResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
    }

    /// <summary>写出一个事件并立即刷出（SSE 要求逐事件推送，不能等缓冲）。</summary>
    public static async Task WriteAsync(
        HttpResponse response, string type, object payload, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        node["type"] = type;
        await response.WriteAsync($"data: {node.ToJsonString()}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// 统一异常兜底：把异常翻译成一个 error 事件。
    /// 返回 false 表示「客户端已主动中断」，此时不应再写出（连接已断，写只会抛）。
    /// </summary>
    public static async Task<bool> HandleErrorAsync(HttpResponse response, Exception ex,
        ILogger logger, string context, CancellationToken ct)
    {
        switch (ex)
        {
            case OperationCanceledException when ct.IsCancellationRequested:
                return false;   // 前端「停止」或关闭弹窗，静默结束

            case OperationCanceledException:
                // 与客户端取消无关 —— 是 AI 服务侧超时
                await WriteAsync(response, SseEvent.Error,
                    new { error = "AI 服务响应超时，请检查接口地址或稍后重试" },
                    CancellationToken.None);
                return true;

            case EsException es:
                await WriteAsync(response, SseEvent.Error, new { error = es.Message }, ct);
                return true;

            default:
                logger.LogError(ex, "{Context}", context);
                await WriteAsync(response, SseEvent.Error,
                    new { error = $"AI 服务调用失败：{ex.Message}" }, ct);
                return true;
        }
    }
}