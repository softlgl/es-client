using System.Text;
using System.Text.Json.Nodes;

namespace EsClient.Api.Services;

/// <summary>
/// AI 提示词集中处。
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

    // ---------- AI 生成查询（POST /api/ai/generate） ----------

    public static string GenerateSystem() => """
        你是 Elasticsearch 查询 DSL 专家。根据用户的中文描述，生成 ES 查询请求体的 JSON，格式为 { "query": { ... } }。
        规则：
        1. 只输出一个 JSON 对象，禁止任何解释文字、markdown 围栏或注释。
        2. 只能使用「字段列表」中给出的字段名（完整路径，如 a.b.c），禁止编造字段。
        3. 类型选型：keyword/ip/version 用 term（多值用 terms）；text 用 match / match_phrase；数值与日期范围用 range；判断字段是否存在用 exists。
        4. text 字段需精确匹配时，若字段列表标注了 keyword 子字段（形如 path.keyword），改用该子字段做 term/terms。
        5. 中文时间词按以下映射转成 ES now 表达式（由 ES 执行时计算，禁止换算为绝对日期）：
        """ + TimeMapping + """
           用户给出绝对日期时使用标准 ISO 格式（如 2024-01-01 或带时间的 ISO 格式）。
        6. 多条件组合用 bool：且→must，或→should 并加 minimum_should_match:1，非→must_not。
        7. 用户明确要求排序/聚合时才添加 sort/aggs 子句，否则只输出 query。
        8. 标注了「nested路径」的字段属于 nested 对象，必须用 nested 查询包裹并指定 path：
           {"nested":{"path":"tags","query":{"term":{"tags.name":"x"}}}}
        示例：
        用户需求：状态是 failed 且重试次数超过 3 次
        输出：{"query":{"bool":{"must":[{"term":{"status":"failed"}},{"range":{"retry_count":{"gt":3}}}]}}}
        用户需求：消息内容精确等于 Connection timeout
        输出：{"query":{"term":{"message.keyword":"Connection timeout"}}}（message 是 text，精确匹配改用 keyword 子字段）
        用户需求：标签名是 urgent 的文档（tags 是 nested 字段）
        输出：{"query":{"nested":{"path":"tags","query":{"term":{"tags.name":"urgent"}}}}}
        """;

    public static string GenerateUser(string index, IndexMeta meta,
        Dictionary<string, string[]> samples, string prompt, string? timeZone)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标索引：{index}（文档数 {meta.DocsCount.ToString("N0")}）");
        if (TryGetLocalNow(timeZone, out var localNow))
            sb.AppendLine($"当前时间：{localNow:yyyy-MM-dd HH:mm}（时区 {timeZone}）。now 表达式按 UTC 截断，date 字段的 range 查询请添加 \"time_zone\":\"{timeZone}\"。");

        AppendColumns(sb, meta, samples);

        if (meta.Columns.Any(c => c.Type is "date" or "date_nanos"))
            sb.AppendLine("（索引含 date 字段，日期需符合 ISO 8601 格式）");
        sb.AppendLine();
        sb.Append($"用户需求：{prompt}");
        return sb.ToString();
    }

    // ---------- AI 问答（POST /api/ai/chat） ----------

    public static string ChatGenerateSystem() => """
        你是 Elasticsearch 数据分析助手。根据用户问题，生成对目标索引执行 search 的请求体 JSON，用于聚合统计或查询明细。
        规则：
        1. 只输出一个 JSON 对象，禁止任何解释文字、markdown 围栏或注释。
        2. 顶层只能包含：query、aggs、size、sort、_source。禁止 script、suggest、knn、runtime_mappings。
        3. 统计类问题（多少、平均、总和、分布、占比、Top N）：size 设为 0，用 aggs 表达；terms 聚合默认 size 20，最大不超过 50。date_histogram 必须带 "format"（按天及以上用 "yyyy-MM-dd"，按小时/分钟用 "yyyy-MM-dd HH:mm"），否则响应里的桶键只有时间戳。
        4. 明细类问题（列出、查看、举例）：size 设 1~10，必要时加 sort（如 {"createTime":{"order":"desc"}}），_source 只保留问题相关字段。
        5. 只能使用「字段列表」中给出的字段名（完整路径），禁止编造字段。
        6. 类型选型：keyword/ip/version 用 term（多值用 terms）；text 用 match / match_phrase，需精确匹配时若标注了 keyword 子字段则改用之；数值与日期范围用 range；判断存在用 exists。
        7. 中文时间词转 ES now 表达式（由 ES 执行时计算，禁止换算为绝对日期）：
        """ + TimeMapping + """
           绝对日期用标准 ISO 格式。
        8. 多条件组合用 bool：且→must，或→should 并加 minimum_should_match:1，非→must_not。
        9. 标注「nested路径」的字段必须用 nested 查询/聚合包裹并指定 path。
        10. 结合上一轮问答历史理解指代（如「再细一点」「换成上个月」）。
        11. 用户消息中若带有「用户当前正在数据浏览中查看的筛选条件」，那是他此刻关注的范围与口径：
            问题未指明范围时沿用它，问题明确指向别的范围时以问题为准，不要被它绑住。
        12. 若问题与「字段列表」里的数据无关（常识、经验观点、闲聊、对 ES 或本工具用法的咨询等），
            不要生成查询，只输出 {"no_query":true}。
        示例：
        问题：各状态的订单数是多少
        输出：{"query":{"match_all":{}},"size":0,"aggs":{"status_count":{"terms":{"field":"status","size":20}}}}
        问题：上个月订单的平均金额
        输出：{"query":{"range":{"createdAt":{"gte":"now-1M/M","lte":"now/M","time_zone":"+08:00"}}},"size":0,"aggs":{"avg_amount":{"avg":{"field":"amount"}}}}
        问题：金额最高的 5 笔订单，列出状态和金额
        输出：{"query":{"match_all":{}},"size":5,"sort":[{"amount":{"order":"desc"}}],"_source":["status","amount"]}
        """;

    /// <summary>
    /// 免查询直接回答的系统提示词：模型在生成阶段判定问题与索引数据无关时
    /// （输出 {"no_query":true}），由本提示词接管回答。与总结阶段的关键区别是
    /// 不禁止观点表达——那个提示词强制「数字只能来自查询结果」，用在常识/经验类
    /// 问题上会让模型无法作答。
    /// </summary>
    public static string ChatDirectSystem() => """
        你是运行在 Elasticsearch 数据客户端里的 AI 助手。用户本轮问题与索引数据无关（常识、经验观点、闲聊、工具用法咨询等），不需要查询数据。
        规则：
        1. 直接用中文简洁回答，控制在 200 字以内。
        2. 可以给出经验性观点，但明确说明这是通用经验判断，不是对该索引统计出来的结论。
        3. 不要编造看似来自查询结果的数字。若用户其实是在问索引里的数据，提醒他换更具体的问法（如「各状态订单数是多少」）。
        """;

    public static string ChatSummarizeSystem() => """
        你是数据分析师。根据 Elasticsearch 查询结果 JSON，用中文简洁回答用户问题。
        规则：
        1. 所有数字必须来自结果 JSON，禁止编造或自行推算结果中不存在的数据。
        2. 结果为空（total 为 0 或无 buckets/rows）时如实说明。
        3. 先给结论，再给关键数字；控制在 150 字以内。
        4. doc_count 是文档数；percentiles / stats 等指标值原样引用，保留其原有精度，不要自行取整或换算单位。
        5. 用户消息中的「此前用户已提问」仅用于理解本轮问题的指代（如「再细一点」「换成上个月」），只回答本轮问题，不要重复回答历史问题。
        6. buckets 内出现 _note 说明 terms 分桶被 size 截断、数据不完整，此时禁止用已列分桶除以 total 得出占比；被问到占比时如实说明分桶不完整。
        """;

    /// <summary>
    /// 总结阶段的用户消息，附上本轮之前若干条提问作为指代锚点。
    /// 总结阶段不像生成阶段那样能读到完整历史，若只发本轮问句，模型无从解析
    /// 「再细一点」「换成上个月」这类追问，回答会与生成阶段的查询口径脱节。
    /// </summary>
    public static string ChatSummarizeUser(string question, JsonNode result,
        List<ChatStore.ChatTurn> historyTurns)
    {
        var sb = new StringBuilder();
        var prior = historyTurns
            .Where(t => t.Role == "user" && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text)
            .TakeLast(4)
            .ToList();
        if (prior.Count > 0)
        {
            sb.AppendLine("此前用户已按顺序提问（仅用于理解本轮问题的指代，不要重复作答）：");
            for (var i = 0; i < prior.Count; i++)
                sb.AppendLine($"{i + 1}. {prior[i]}");
            sb.AppendLine();
        }
        sb.Append($"本轮问题：{question}\n\nES 查询结果 JSON：\n{result.ToJsonString()}");
        return sb.ToString();
    }

    public static string ChatGenerateUser(string index, IndexMeta meta,
        Dictionary<string, string[]> samples, string question, string? timeZone, string? currentQuery)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标索引：{index}（文档数 {meta.DocsCount.ToString("N0")}）");
        if (TryGetLocalNow(timeZone, out var localNow))
            sb.AppendLine($"当前时间：{localNow:yyyy-MM-dd HH:mm}（时区 {timeZone}）。date 字段的 range 查询请添加 \"time_zone\":\"{timeZone}\"。");

        AppendColumns(sb, meta, samples);

        if (!string.IsNullOrWhiteSpace(currentQuery))
        {
            // 浏览上下文：说明它是「参考」而非「要复现的目标」，否则模型容易直接照抄这份筛选条件，
            // 导致用户问别的口径时仍被历史条件绑住
            sb.AppendLine("用户当前正在「数据浏览」中查看以下筛选条件（仅供理解他关注的范围与口径，不要机械照搬；本轮问题优先）：");
            sb.AppendLine(currentQuery);
        }
        sb.AppendLine();
        sb.Append($"用户问题：{question}");
        return sb.ToString();
    }

    // ---------- 两条链路共用的渲染片段 ----------

    /// <summary>
    /// 时区标识 → 该时区的当前时间。未知标识时返回 false（调用方退回 UTC）。
    /// 两条链路都需要「告诉模型此刻是什么时间」，否则「今天」「上个月」无从落地。
    /// </summary>
    private static bool TryGetLocalNow(string? timeZone, out DateTimeOffset localNow)
    {
        localNow = DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(timeZone)) return false;
        try
        {
            var tzi = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzi);
            return true;
        }
        catch { return false; }   // 未知时区标识则退回 UTC 时间
    }

    /// <summary>渲染字段清单（字段名 | 类型 | keyword 子字段 | nested 路径 | 示例值）。</summary>
    private static void AppendColumns(StringBuilder sb, IndexMeta meta, Dictionary<string, string[]> samples)
    {
        sb.AppendLine("字段列表（字段名 | 类型[| keyword子字段][| 示例值]）：");
        foreach (var c in meta.Columns)
        {
            var line = $"- {c.Path} | {c.Type}";
            if (c.Type == "text" && c.KeywordPath != null) line += $" | keyword子字段: {c.KeywordPath}";
            var nestedPath = meta.ResolveNestedPath(c.Path);
            if (nestedPath != null) line += $" | nested路径: {nestedPath}";
            if (samples.TryGetValue(c.KeywordPath ?? c.Path, out var vals))
                line += $" | 示例值: {string.Join(", ", vals)}";
            sb.AppendLine(line);
        }
    }
}