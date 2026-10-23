namespace EsClient.Api.Services;

/// <summary>
/// AI 流式（SSE）协议事件名的唯一来源。这些字符串是前后端的隐式契约：后端由 <see cref="AiClient"/>
/// （产出 reasoning/content/status）与两个 AI 控制器（其余事件）写出，前端 ai-chat.js / ai-dialog.js
/// 按同名 switch 渲染。此前 8 个事件名在后端多处以裸字符串手抄，改协议极易漏改；集中于此至少保证
/// 后端内部一致。⚠️ 前端是独立 JS，无法共享该常量，改动事件名时须同步前端（见 wwwroot/js/views）。
/// </summary>
internal static class SseEvent
{
    public const string Reasoning = "reasoning";
    public const string Content = "content";
    public const string Status = "status";
    public const string Dsl = "dsl";
    public const string Data = "data";
    public const string Direct = "direct";
    public const string Done = "done";
    public const string Error = "error";
}
