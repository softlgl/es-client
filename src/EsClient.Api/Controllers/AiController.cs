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

    public record SaveSettingsRequest(string? Id, string Provider, string BaseUrl, string ApiKey, string Model,
        string? ThinkingModel, bool? JsonMode);
    public record ChatMsg(string Role, string Content);
    public record GenerateRequest(string Index, string Prompt, List<ChatMsg>? History, bool? Thinking,
        string? LastError, string? TimeZone);
    public record CountRequest(string Index, JsonObject Dsl);

    /// <summary>试运行生成的 DSL（size:0），返回命中总数，供前端展示「命中 N 条」。</summary>
    [HttpPost("/api/ai/count")]
    public async Task<IActionResult> Count([FromBody] CountRequest req, CancellationToken ct)
    {
        // 索引名来自请求体，可能缺失 —— 先归一，后续所有使用都基于非空值
        var index = req.Index ?? "";
        if (!IEsClient.IsValidIndexName(index))
            return BadRequest(new { error = $"非法索引名：{index}" });
        if (req.Dsl?["query"] is not JsonObject query)
            return BadRequest(new { error = "缺少 query 子句" });

        var client = _store.Require();
        var res = await client.SearchAsync(index, new JsonObject
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
                thinkingModel = m.ThinkingModel,
                jsonMode = m.JsonMode
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
            ThinkingModel = string.IsNullOrWhiteSpace(req.ThinkingModel) ? null : req.ThinkingModel.Trim(),
            JsonMode = req.JsonMode
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
        // 索引名来自请求体，可能缺失 —— 先归一，后续所有使用都基于非空值
        var index = req.Index ?? "";
        if (!IEsClient.IsValidIndexName(index))
        {
            Response.StatusCode = 400;
            await Response.WriteAsJsonAsync(new { error = $"非法索引名：{index}" });
            return;
        }

        SseStream.Begin(Response);

        try
        {
            var client = _store.Require();
            var meta = await _mapping.GetMetaAsync(index, ct);

            var samplingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            samplingCts.CancelAfter(TimeSpan.FromSeconds(5)); // 采样失败不阻塞生成
            var samples = await _aiClient.SampleFieldValuesAsync(client, index, meta, samplingCts.Token);

            var messages = new List<object>
            {
                new { role = "system", content = AiPrompts.GenerateSystem() }
            };
            // 多轮修正：按完整轮次（user→assistant）成对截断，避免只剩半轮使指代失效
            foreach (var m in TakeLastTurns(req.History ?? [], 4))
                messages.Add(new { role = m.Role, content = m.Content });
            // 错误自愈：把上一次的失败原因回传给模型，让它自行修正
            if (!string.IsNullOrWhiteSpace(req.LastError))
                messages.Add(new { role = "user", content = $"上一次生成失败，错误信息：{req.LastError}。请修正后重新输出，仍须遵守全部规则。" });
            messages.Add(new { role = "user", content = AiPrompts.GenerateUser(index, meta, samples, req.Prompt.Trim(), req.TimeZone) });

            var content = await _aiClient.StreamAsync(messages, req.Thinking == true,
                (type, text) => WriteSseAsync(type, new { text }, ct), ct, jsonOutput: true);
            var dsl = AiDslGuard.ExtractQueryDsl(content);
            if (dsl != null)
                await WriteSseAsync("done", new { dsl }, ct);
            else
            {
                _logger.LogWarning("AI 返回内容无法解析为 JSON：{Content}", content.Length > 300 ? content[..300] : content);
                await WriteSseAsync("error", new { error = "AI 返回的内容不是有效 JSON，请重试或调整描述" }, ct);
            }
        }
        catch (Exception ex)
        {
            await SseStream.HandleErrorAsync(Response, ex, _logger, "AI 生成失败", ct);
        }
    }

    /// <summary>安全读取 JsonNode 字符串（缺失 / JSON null / 非字符串均返回 null）。</summary>
    private static string? NodeStr(JsonNode? node) => AiClient.NodeStr(node);

    private Task WriteSseAsync(string type, object payload, CancellationToken ct) =>
        SseStream.WriteAsync(Response, type, payload, ct);

    

    }
