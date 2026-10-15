using System.Net.Http.Headers;
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
    private readonly ConnectionStore _store;
    private readonly MappingService _mapping;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AiController> _logger;

    public AiController(AiSettingsStore ai, ConnectionStore store, MappingService mapping,
        IHttpClientFactory httpFactory, ILogger<AiController> logger)
    {
        _ai = ai;
        _store = store;
        _mapping = mapping;
        _httpFactory = httpFactory;
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
            var samples = await SampleFieldValuesAsync(client, req.Index, meta, samplingCts.Token);

            var messages = new List<object>
            {
                new { role = "system", content = BuildSystemPrompt() }
            };
            foreach (var m in (req.History ?? []).TakeLast(8))
            {
                if (m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content))
                    messages.Add(new { role = m.Role, content = m.Content });
            }
            // 错误自愈：把上一次的失败原因回传给模型，让它自行修正
            if (!string.IsNullOrWhiteSpace(req.LastError))
                messages.Add(new { role = "user", content = $"上一次生成失败，错误信息：{req.LastError}。请修正后重新输出，仍须遵守全部规则。" });
            messages.Add(new { role = "user", content = BuildUserPrompt(req.Index, meta, samples, req.Prompt.Trim(), req.TimeZone) });

            var content = await StreamUpstreamAsync(messages, req.Thinking == true, ct);
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

    /// <summary>以 stream 模式调用上游 OpenAI 兼容接口，逐块转发 reasoning/content，返回完整 content。thinking 为 true 时优先使用配置的思考模型。</summary>
    private async Task<string> StreamUpstreamAsync(List<object> messages, bool thinking, CancellationToken ct)
    {
        var s = _ai.Get();
        var http = _httpFactory.CreateClient("ai");
        http.Timeout = Timeout.InfiniteTimeSpan; // 读流时长自行控制

        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        streamCts.CancelAfter(TimeSpan.FromMinutes(4));

        var model = thinking && !string.IsNullOrWhiteSpace(s.ThinkingModel) ? s.ThinkingModel : s.Model;
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{s.BaseUrl.TrimEnd('/')}/chat/completions");
        reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray([.. messages.Select(m => JsonSerializer.SerializeToNode(m))]),
            ["temperature"] = 0.2,
            ["stream"] = true
        };
        reqMsg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, streamCts.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new EsException($"无法连接 AI 服务（{s.BaseUrl}）：{ex.Message}", 502);
        }
        catch (TaskCanceledException) when (!streamCts.Token.IsCancellationRequested)
        {
            throw new EsException("AI 服务响应超时，请检查接口地址或稍后重试", 504);
        }

        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(streamCts.Token);
            var detail = TryExtractOpenAiError(text) ?? text;
            res.Dispose();
            throw new EsException(
                $"AI 服务返回错误（{(int)res.StatusCode}）：{detail[..Math.Min(detail.Length, 200)]}", 502);
        }

        var contentBuilder = new StringBuilder();
        try
        {
            using var stream = await res.Content.ReadAsStreamAsync(streamCts.Token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(streamCts.Token) is { } line)
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
                var reasoning = NodeStr(delta["reasoning_content"]) ?? NodeStr(delta["reasoning"]);
                if (!string.IsNullOrEmpty(reasoning))
                    await WriteSseAsync("reasoning", new { text = reasoning }, streamCts.Token);

                var piece = NodeStr(delta["content"]);
                if (!string.IsNullOrEmpty(piece))
                {
                    contentBuilder.Append(piece);
                    await WriteSseAsync("content", new { text = piece }, streamCts.Token);
                }
            }
        }
        finally
        {
            res.Dispose();
        }
        return contentBuilder.ToString();
    }

    /// <summary>安全读取 JsonNode 字符串（缺失 / JSON null / 非字符串均返回 null）。</summary>
    private static string? NodeStr(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private async Task WriteSseAsync(string type, object payload, CancellationToken ct)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        node["type"] = type;
        await Response.WriteAsync($"data: {node.ToJsonString()}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }

    /// <summary>对可精确匹配的字段做 terms 聚合采样（每字段 top 5），供 AI 选择真实存在的值。</summary>
    private async Task<Dictionary<string, string[]>> SampleFieldValuesAsync(
        EsHttpClient client, string index, IndexMeta meta, CancellationToken ct)
    {
        try
        {
            var fields = meta.Columns
                .Where(c => c.Type is "keyword" or "ip" or "version" ||
                            (c.Type == "text" && c.KeywordPath != null))
                .Select(c => c.KeywordPath ?? c.Path)
                .Distinct()
                .Take(12)
                .ToList();
            var numeric = meta.Columns
                .Where(c => c.Type is "long" or "integer" or "short" or "byte" or "double" or "float" or "half_float" or "scaled_float")
                .Select(c => c.Path)
                .Take(4);
            fields.AddRange(numeric.Except(fields));
            var dates = meta.Columns
                .Where(c => c.Type is "date" or "date_nanos")
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "字段值采样失败（不影响生成）：{Index}", index);
            return new Dictionary<string, string[]>();
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

    private static string BuildSystemPrompt() => """
        你是 Elasticsearch 查询 DSL 专家。根据用户的中文描述，生成 ES 查询请求体的 JSON，格式为 { "query": { ... } }。
        规则：
        1. 只输出一个 JSON 对象，禁止任何解释文字、markdown 围栏或注释。
        2. 只能使用「字段列表」中给出的字段名（完整路径，如 a.b.c），禁止编造字段。
        3. 类型选型：keyword/ip/version 用 term（多值用 terms）；text 用 match / match_phrase；数值与日期范围用 range；判断字段是否存在用 exists。
        4. text 字段需精确匹配时，若字段列表标注了 keyword 子字段（形如 path.keyword），改用该子字段做 term/terms。
        5. 中文时间词按以下映射转成 ES now 表达式（由 ES 执行时计算，禁止换算为绝对日期）：
           今天→now/d  昨天→now-1d/d  本周→now-1w/w  本月→now/M  今年→now-1y/y
           最近N天→now-Nd/d  近一个月→now-1M/M  近一年→now-1y/y  N天前→now-Nd/d
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

    private static string? TryExtractOpenAiError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?["message"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }

    /// <summary>从模型输出中提取查询 DSL：剥 markdown 围栏 → 截取 JSON → 规范为 { query: ... }。</summary>
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

        if (obj["query"] is JsonObject q) return new JsonObject { ["query"] = q.DeepClone() };

        // 裸 query 子句：包含常见查询关键字时包一层 query
        if (obj.Any(kvp => QueryRootKeys.Contains(kvp.Key)))
            return new JsonObject { ["query"] = obj.DeepClone() };

        return null;
    }
}
