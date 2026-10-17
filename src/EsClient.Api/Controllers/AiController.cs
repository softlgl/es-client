using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsClient.Api.Controllers;

[ApiController]
public class AiController : ControllerBase
{
    private readonly AiSettingsStore _ai;
    private readonly AiClient _aiClient;
    private readonly ConnectionStore _store;
    private readonly MappingService _mapping;
    private readonly ILogger<AiController> _logger;

    public AiController(AiSettingsStore ai, AiClient aiClient, ConnectionStore store, MappingService mapping,
        ILogger<AiController> logger)
    {
        _ai = ai;
        _aiClient = aiClient;
        _store = store;
        _mapping = mapping;
        _logger = logger;
    }

    public record SaveSettingsRequest(string? Id, string Provider, string BaseUrl, string ApiKey, string Model, string? ThinkingModel);
    public record ChatMsg(string Role, string Content);
    public record GenerateRequest(string Index, string Prompt, List<ChatMsg>? History, bool? Thinking,
        string? LastError, string? TimeZone);
    public record CountRequest(string Index, JsonObject Dsl);

    /// <summary>试运行生成的 DSL（size:0），返回命中总数，供前端展示「命中 N 条」。</summary>
    [HttpPost("/api/ai/count")]
    public async Task<IActionResult> Count([FromBody] CountRequest req, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(req.Index ?? ""))
            return BadRequest(new { error = $"非法索引名：{req.Index}" });
        if (req.Dsl?["query"] is not JsonObject query)
            return BadRequest(new { error = "缺少 query 子句" });

        var client = _store.Require();
        var res = await client.SearchAsync(req.Index, new JsonObject
        {
            ["size"] = 0,
            ["track_total_hits"] = true,
            ["query"] = query.DeepClone()
        }, ct);
        var total = res["hits"]?["total"]?["value"]?.GetValue<long>() ?? 0;
        return Ok(new { total });
    }

    // ---------- 解析与安全约束 ----------

    /// <summary>
    /// 取历史末尾 maxTurns 个完整轮次（以 assistant 为轮次边界向前成对取）。
    /// 按条数截断会把「用户需求 / 模型输出」切成半轮，多轮修正的指代就失去了上下文。
    /// </summary>
    private static IEnumerable<ChatMsg> TakeLastTurns(List<ChatMsg> history, int maxTurns)
    {
        var end = history.FindLastIndex(m => m.Role == "assistant") + 1;
        if (end == 0) end = history.Count; // 无 assistant 记录则退化为按条数取
        var start = Math.Max(0, end - maxTurns * 2);
        return history.Skip(start).Take(end - start)
            .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content));
    }

    private static readonly string[] QueryRootKeys =
    [
        "bool", "match", "match_phrase", "match_phrase_prefix", "multi_match", "term", "terms",
        "range", "exists", "wildcard", "prefix", "fuzzy", "ids", "nested", "query_string",
        "match_all", "match_none", "function_score", "simple_query_string", "regexp", "span_near"
    ];

    [HttpGet("/api/ai/settings")]
    public IActionResult Settings()
    {
        return Ok(new
        {
            configured = _ai.IsConfigured,
            activeId = _ai.ActiveId,
            models = _ai.List().Select(m => new
            {
                id = m.Id,
                provider = m.Provider,
                baseUrl = m.BaseUrl,
                apiKey = m.ApiKey,
                model = m.Model,
                thinkingModel = m.ThinkingModel
            }),
            presets = AiSettingsStore.Presets.ToDictionary(
                p => p.Key.ToLowerInvariant(), p => new
                {
                    label = p.Value.Label,
                    baseUrl = p.Value.BaseUrl,
                    model = p.Value.Model,
                    thinkingModel = p.Value.ThinkingModel
                })
        });
    }

    [HttpPost("/api/ai/settings")]
    public IActionResult SaveSettings([FromBody] SaveSettingsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.BaseUrl) || string.IsNullOrWhiteSpace(req.Model))
            return BadRequest(new { error = "接口地址与模型名不能为空" });
        if (!Uri.TryCreate(req.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return BadRequest(new { error = $"接口地址不合法：{req.BaseUrl}" });

        var provider = string.IsNullOrWhiteSpace(req.Provider) ? "custom" : req.Provider.Trim().ToLowerInvariant();
        if (!AiSettingsStore.Presets.ContainsKey(provider)) provider = "custom";

        var saved = _ai.Save(new AiModelConfig
        {
            Id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id!.Trim(),
            Provider = provider,
            BaseUrl = req.BaseUrl.Trim().TrimEnd('/'),
            ApiKey = req.ApiKey?.Trim() ?? "",
            Model = req.Model.Trim(),
            ThinkingModel = string.IsNullOrWhiteSpace(req.ThinkingModel) ? null : req.ThinkingModel.Trim()
        });
        return Ok(new { ok = true, id = saved.Id });
    }

    public record SwitchSettingsRequest(string Id);

    [HttpPost("/api/ai/settings/switch")]
    public IActionResult SwitchSettings([FromBody] SwitchSettingsRequest req)
    {
        if (!_ai.Switch(req.Id))
            return BadRequest(new { error = "配置不存在或不完整，无法切换" });
        return Ok(new { ok = true });
    }

    [HttpPost("/api/ai/settings/delete")]
    public IActionResult DeleteSettings([FromBody] SwitchSettingsRequest req)
    {
        _ai.Delete(req.Id);
        return Ok(new { ok = true });
    }

    /// <summary>
    /// 自然语言 → ES 查询 DSL（SSE 流式）。实时转发模型的思考过程（reasoning_content）
    /// 与生成内容，流结束后解析 DSL 并推送 done/error 事件。
    /// 事件序列：reasoning* / content* → done{dsl} 或 error{error}。
    /// </summary>
    [HttpPost("/api/ai/generate")]
    public async Task Generate([FromBody] GenerateRequest req, CancellationToken ct)
    {
        // 校验阶段仍是普通 JSON（前端用 response.ok 区分）
        if (!_ai.IsConfigured)
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = "尚未配置 AI 服务，请先在「AI 设置」中填写接口地址与密钥" });
            return;
        }
        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = "请输入需求描述" });
            return;
        }
        if (!IEsClient.IsValidIndexName(req.Index ?? ""))
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = $"非法索引名：{req.Index}" });
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";

        try
        {
            var client = _store.Require();
            var meta = await _mapping.GetMetaAsync(req.Index, ct);

            var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            samplingCts.CancelAfter(TimeSpan.FromSeconds(5)); // 采样失败不阻塞生成
            var samples = await _aiClient.SampleFieldValuesAsync(client, req.Index, meta, samplingCts.Token);

            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt() }
            };
            // 多轮修正：按完整轮次（user→assistant）成对截断，避免只剩半轮使指代失效
            foreach (var m in TakeLastTurns(req.History ?? [], 4))
                messages.Add(new { role = m.Role, content = m.Content });
            // 错误自愈：把上一次的失败原因回传给模型，让它自行修正
            if (!string.IsNullOrWhiteSpace(req.LastError))
                messages.Add(new { role = "user", content = $"上一次生成失败，错误信息：{req.LastError}。请修正后重新输出，仍须遵守全部规则。" });
            messages.Add(new { role = "user", content = BuildUserPrompt(req.Index, meta, samples, req.Prompt.Trim(), req.TimeZone) });

            var content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                (type, text) => WriteSseAsync(type, new { text }, ct), ct);
            var dsl = ExtractQueryDsl(content);
            if (dsl != null)
                await WriteSseAsync("done", new { dsl }, ct);
            else
            {
                _logger.LogWarning("AI 返回内容无法解析为 JSON：{Content}", content.Length > 300 ? content[..300] : content);
                await WriteSseAsync("error", new { error = "AI 返回的内容不是有效 JSON，请重试或调整描述" }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // 客户端已中断（前端「停止」/关闭弹窗），静默结束
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await WriteSseAsync("error", new { error = "AI 服务响应超时，请检查接口地址或稍后重试" }, CancellationToken.None);
        }
        catch (EsException ex)
        {
            await WriteSseAsync("error", new { error = ex.Message }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI 生成失败");
            await WriteSseAsync("error", new { error = $"AI 服务调用失败：{ex.Message}" }, ct);
        }
    }

    /// <summary>安全读取 JsonNode 字符串（缺失 / JSON null / 非字符串均返回 null）。</summary>
    private static string? NodeStr(JsonNode? node) => AiClient.NodeStr(node);

    private async Task WriteSseAsync(string type, object payload, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        node["type"] = type;
        await Response.WriteAsync($"data: {node.ToJsonString()}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    private static string BuildSystemPrompt() => """
        你是 Elasticsearch 查询 DSL 专家。根据用户的中文描述，生成 ES 查询请求体的 JSON，格式为 { "query": { ... } }。
        规则：
        1. 只输出一个 JSON 对象，禁止任何解释文字、markdown 围栏或注释。
        2. 只能使用「字段列表」中给出的字段名（完整路径，如 a.b.c），禁止编造字段。
        3. 类型选型：keyword/ip/version 用 term（多值用 terms）；text 用 match / match_phrase；数值与日期范围用 range；判断字段是否存在用 exists。
        4. text 字段需精确匹配时，若字段列表标注了 keyword 子字段（形如 path.keyword），改用该子字段做 term/terms。
        5. 中文时间词按以下映射转成 ES now 表达式（由 ES 执行时计算，禁止换算为绝对日期）：
        """ + AiPrompts.TimeMapping + """
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

    private static string BuildUserPrompt(string index, IndexMeta meta,
        Dictionary<string, string[]> samples, string prompt, string? timeZone)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标索引：{index}（文档数 {meta.DocsCount.ToString("N0")}）");
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            var localNow = DateTimeOffset.UtcNow;
            try
            {
                var tzi = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
                localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tzi);
            }
            catch { /* 未知时区标识则退回 UTC 时间 */ }
            sb.AppendLine($"当前时间：{localNow:yyyy-MM-dd HH:mm}（时区 {timeZone}）。now 表达式按 UTC 截断，date 字段的 range 查询请添加 \"time_zone\":\"{timeZone}\"。");
        }
        sb.AppendLine("字段列表（字段名 | 类型[| keyword子字段][| 示例值]）：");
        foreach (var c in meta.Columns)
        {
            var field = c.KeywordPath ?? c.Path;
            var line = $"- {c.Path} | {c.Type}";
            if (c.Type == "text" && c.KeywordPath != null) line += $" | keyword子字段: {c.KeywordPath}";
            var nestedPath = meta.NestedPaths
                .Where(p => c.Path == p || c.Path.StartsWith(p + "."))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();
            if (nestedPath != null) line += $" | nested路径: {nestedPath}";
            if (samples.TryGetValue(field, out var vals))
                line += $" | 示例值: {string.Join(", ", vals)}";
            sb.AppendLine(line);
        }
        if (meta.Columns.Any(c => c.Type is "date" or "date_nanos"))
            sb.AppendLine("（索引含 date 字段，日期需符合 ISO 8601 格式）");
        sb.AppendLine();
        sb.Append($"用户需求：{prompt}");
        return sb.ToString();
    }

    /// <summary>
    /// 从模型输出中提取查询 DSL：剥 markdown 围栏 → 截取 JSON → 规范为完整请求体。
    /// query/sort/size/aggs/_source 等顶层键全部保留，避免丢失模型的排序与取数条数语义。
    /// </summary>
    private static JsonObject? ExtractQueryDsl(string content)
    {
        var text = content.Trim();
        var fenceStart = text.IndexOf("```", StringComparison.Ordinal);
        while (fenceStart >= 0)
        {
            var lineEnd = text.IndexOf('\n', fenceStart);
            if (lineEnd < 0) break;
            var fenceEnd = text.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (fenceEnd < 0) break;
            var inner = text[(lineEnd + 1)..fenceEnd].Trim();
            if (inner.StartsWith('{')) { text = inner; break; }
            fenceStart = text.IndexOf("```", fenceEnd + 3, StringComparison.Ordinal);
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(text[start..(end + 1)]); }
        catch { return null; }
        if (node is not JsonObject obj) return null;

        // 已含 query：整体保留（sort/size/aggs 等顶层键随行）
        if (obj["query"] is JsonObject)
            return (JsonObject)obj.DeepClone();

        // 裸 query 子句：包一层 query，同级 sort 一并提级
        if (obj.Any(kvp => QueryRootKeys.Contains(kvp.Key)))
        {
            JsonNode? sort = null;
            if (obj["sort"] != null) { sort = obj["sort"]!.DeepClone(); obj.Remove("sort"); }
            var wrapped = new JsonObject { ["query"] = obj.DeepClone() };
            if (sort != null) wrapped["sort"] = sort;
            return wrapped;
        }

        // 模型省略 query 只给了 sort/size/aggs：补 match_all 保住语义
        if (obj.Any(kvp => kvp.Key is "sort" or "size" or "aggs" or "aggregations" or "_source"))
        {
            obj["query"] = new JsonObject { ["match_all"] = new JsonObject() };
            return obj;
        }

        return null;
    }
}
