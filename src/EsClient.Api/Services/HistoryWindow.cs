namespace EsClient.Api.Services;

/// <summary>
/// 历史上下文窗口：以最后一个 assistant 消息为轮次边界向前成对截断，保证截出的片段始终是
/// 完整的 user→assistant 轮，不会把「多轮修正」的指代（「再细一点」）切成只剩半轮而失去上下文。
/// AI 生成（AiController）与 AI 问答（AiChatController）两条链路共用同一算法，
/// 此前各自手抄一份「找末 assistant + 按 2×轮数回退」的边界处理，改一处极易漏另一处。
/// </summary>
internal static class HistoryWindow
{
    /// <summary>
    /// 取最近 <paramref name="maxTurns"/> 个完整轮次（= 至多 2×maxTurns 条）在 [start, end) 区间内的切片。
    /// end = 最后一个 assistant 的下标 +1（一条 assistant 都没有时退化为按条数取到末尾）；
    /// start = max(0, end - 2×maxTurns)。<paramref name="roleOf"/> 从元素取角色字符串（"assistant"/"user"…）。
    /// </summary>
    public static List<T> Take<T>(IReadOnlyList<T> history, int maxTurns, Func<T, string> roleOf)
    {
        var end = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (roleOf(history[i]) == "assistant") { end = i + 1; break; }
        }
        if (end < 0) end = history.Count; // 无 assistant 记录则退化为按条数取
        var start = Math.Max(0, end - maxTurns * 2);
        var window = new List<T>(end - start);
        for (var i = start; i < end; i++) window.Add(history[i]);
        return window;
    }
}
