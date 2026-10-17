namespace EsClient.Api.Services;

/// <summary>
/// AI 提示词中共用的规则片段。
/// 「AI 生成查询」与「AI 问答」是两条独立链路，但共享同一批 DSL 规则（字段限制、类型选型、
/// 中文时间词映射等）；规则文本一旦各写一份就会漂移，导致同一句中文描述在两个入口生成出
/// 不同结果，因此集中在此处，由两条链路引用同一份文本。
/// </summary>
public static class AiPrompts
{
    /// <summary>
    /// 中文时间词 → ES now 表达式的映射表。末尾带换行，便于在 system prompt 的规则列表中
    /// 拼接成「规则标题 + 映射表 + 补充说明」的多行条目。
    /// </summary>
    public const string TimeMapping =
        "   今天→now/d  昨天→now-1d/d  本周→now-1w/w  本月→now/M  今年→now-1y/y\n" +
        "   最近N天→now-Nd/d  近一个月→now-1M/M  近一年→now-1y/y  N天前→now-Nd/d\n";
}
