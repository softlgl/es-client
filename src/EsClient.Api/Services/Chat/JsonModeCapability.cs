using System.Collections.Concurrent;

namespace EsClient.Api.Services.Chat;

/// <summary>
/// JSON 模式（response_format=json_object）的跨层共享约定与能力档案。
/// 门面据此决定本次请求带不带该参数，降级中间件据此记住端点拒绝过的结论，
/// 档案键含 baseUrl+model 且为进程级静态 —— 与改造前语义一致：配置编辑重建
/// 客户端链后结论不丢，重启后最多再探测一次。
/// </summary>
internal static class JsonModeCapability
{
    /// <summary>
    /// ChatOptions.AdditionalProperties 的保留键：存在即表示本次请求要带
    /// response_format=json_object，connector 读后拼进请求体，降级中间件重试时移除。
    /// （MEAI 对 provider 私有参数走 AdditionalProperties 的官方口子）
    /// </summary>
    public const string ResponseFormatKey = "esclient.response_format";

    /// <summary>已确认不支持 response_format 的 baseUrl+model。</summary>
    public static readonly ConcurrentDictionary<string, bool> Rejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>能力档案的键：同一端点不同模型的支持度可能不同，故模型名也要进键。</summary>
    public static string EndpointKey(string baseUrl, string model) => $"{baseUrl.TrimEnd('/')}|{model}";

    /// <summary>
    /// 判断 4xx 是否由 response_format 引起。宁可漏判（照常报错）不误判：降级重试最多一次，
    /// 且降级后仍失败会照常抛错，误判的代价只是多发一次请求，不会掩盖真实错误。
    /// </summary>
    public static bool LooksLikeRejected(int status, string detail)
    {
        if (status is not (400 or 422 or 501)) return false;
        var d = detail.ToLowerInvariant();
        return d.Contains("response_format") || d.Contains("json_object") || d.Contains("json mode")
            || d.Contains("unsupported") || d.Contains("not supported")
            || d.Contains("unknown parameter") || d.Contains("unrecognized");
    }
}
