using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;

namespace EsClient.Api.Services;

/// <summary>
/// OpenAI 兼容接口调用封装：流式对话与字段值采样。供 AI 生成查询、AI 问答等功能共用。
/// </summary>
public class AiClient(IHttpClientFactory httpFactory, AiSettingsStore ai)
{
    public bool IsConfigured => ai.IsConfigured;

    /// <summary>
    /// 进程内的端点能力档案：已确认不支持 response_format 的 baseUrl+model。
    /// 只记在内存、不写回配置文件，重启后最多再探测一次；用户在 AI 设置里显式指定的
    /// 「强制开启/关闭」优先于这里的结论。
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool> JsonModeRejected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 以 stream 模式调用上游接口，逐块回调 onEvent("reasoning"/"content", text)，返回完整 content。
    /// thinking 为 true 时优先使用配置的思考模型。
    /// jsonOutput 为 true 表示本次要求模型输出 JSON，请求会带 response_format=json_object：
    /// 能显著降低围栏/语法错误导致的解析失败，但端点支持度不统一，故不支持时自动降级重试一次
    /// （结论记入能力档案）。总结、直接回答这类自由文本调用不要开。
    /// </summary>
    public async Task<string> StreamAsync(List<object> messages, bool thinking,
        Func<string, string, Task>? onEvent, CancellationToken ct, bool jsonOutput = false)
    {
        var s = ai.Get();
        var http = httpFactory.CreateClient("ai");
        http.Timeout = Timeout.InfiniteTimeSpan; // 读流时长自行控制

        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        streamCts.CancelAfter(TimeSpan.FromMinutes(4));

        var model = thinking && !string.IsNullOrWhiteSpace(s.ThinkingModel) ? s.ThinkingModel : s.Model;
        var withJson = UseJsonMode(s, model, thinking, jsonOutput);

        // 发送。带 json_object 且端点拒绝该参数时降级重试一次，最多一次
        HttpResponseMessage res;
        while (true)
        {
            using var reqMsg = BuildRequest(s, model, messages, withJson);
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

            if (res.IsSuccessStatusCode || !withJson) break;

            var status = (int)res.StatusCode;
            var errText = await res.Content.ReadAsStringAsync(streamCts.Token);
            var errDetail = TryExtractOpenAiError(errText) ?? errText;
            res.Dispose();
            // 用户显式「强制开启」时不擅自降级：让错误直接暴露到界面，由用户决定
            if (s.JsonMode == true || !LooksLikeJsonModeRejected(status, errDetail))
                throw new EsException($"AI 服务返回错误（{status}）：{errDetail[..Math.Min(errDetail.Length, 200)]}", 502);
            JsonModeRejected[EndpointKey(s, model)] = true;
            withJson = false;
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
                if (!string.IsNullOrEmpty(reasoning) && onEvent != null)
                    await onEvent("reasoning", reasoning);

                var piece = NodeStr(delta["content"]);
                if (!string.IsNullOrEmpty(piece))
                {
                    contentBuilder.Append(piece);
                    if (onEvent != null) await onEvent("content", piece);
                }
            }
        }
        finally
        {
            res.Dispose();
        }
        return contentBuilder.ToString();
    }

    /// <summary>
    /// 本次请求是否带 response_format=json_object。三条约束：
    /// ① 只有要求输出 JSON 的调用才开（总结/直接回答是自由文本，开了反而被约束成对象）；
    /// ② 思考模式一律不开——部分推理模型（R1、qwen-thinking）在 JSON 模式下可能返回空 content；
    /// ③ 端点已确认不支持则跳过。
    /// </summary>
    private static bool UseJsonMode(AiModelConfig s, string model, bool thinking, bool jsonOutput)
    {
        if (!jsonOutput || thinking || s.JsonMode == false) return false;
        return s.JsonMode == true || !JsonModeRejected.ContainsKey(EndpointKey(s, model));
    }

    /// <summary>能力档案的键：同一端点不同模型的支持度可能不同，故模型名也要进键。</summary>
    private static string EndpointKey(AiModelConfig s, string model) => $"{s.BaseUrl.TrimEnd('/')}|{model}";

    /// <summary>
    /// 判断 4xx 是否由 response_format 引起。宁可漏判（照常报错）不误判：降级重试最多一次，
    /// 且降级后仍失败会照常抛错，误判的代价只是多发一次请求，不会掩盖真实错误。
    /// </summary>
    private static bool LooksLikeJsonModeRejected(int status, string detail)
    {
        if (status is not (400 or 422 or 501)) return false;
        var d = detail.ToLowerInvariant();
        return d.Contains("response_format") || d.Contains("json_object") || d.Contains("json mode")
            || d.Contains("unsupported") || d.Contains("not supported")
            || d.Contains("unknown parameter") || d.Contains("unrecognized");
    }

    private static HttpRequestMessage BuildRequest(AiModelConfig s, string model,
        List<object> messages, bool withJson)
    {
        var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{s.BaseUrl.TrimEnd('/')}/chat/completions");
        reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray([.. messages.Select(m => JsonSerializer.SerializeToNode(m))]),
            ["temperature"] = 0.2,
            ["stream"] = true
        };
        // OpenAI 系还要求 messages 里出现 "json" 字样才允许该参数，本项目的生成类提示词均已包含
        if (withJson)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        reqMsg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return reqMsg;
    }

    /// <summary>对可精确匹配的字段做 terms/stats 聚合采样，供 AI 选择真实存在的字段与值。</summary>
    public async Task<Dictionary<string, string[]>> SampleFieldValuesAsync(
        EsHttpClient client, string index, IndexMeta meta, CancellationToken ct)
    {
        try
        {
            // nested 路径下的字段不能在本层聚合里采：根级 terms/stats 看不到嵌套文档的值，
            // 采出来必然是空，白白占用下方有限的名额，故直接排除（其示例值由 nested 聚合另行提供）
            bool IsNested(ColumnMeta c) => meta.IsNested(c.Path);

            var fields = meta.Columns
                .Where(c => !IsNested(c) && (c.Type is "keyword" or "ip" or "version" ||
                            (c.Type == "text" && c.KeywordPath != null)))
                .Select(c => c.KeywordPath ?? c.Path)
                .Distinct()
                .Take(12)
                .ToList();
            var numeric = meta.Columns
                .Where(c => !IsNested(c) && c.Type is "long" or "integer" or "short" or "byte" or "double" or "float" or "half_float" or "scaled_float")
                .Select(c => c.Path)
                .Take(4);
            fields.AddRange(numeric.Except(fields));
            var dates = meta.Columns
                .Where(c => !IsNested(c) && c.Type is "date" or "date_nanos")
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
        catch
        {
            return new Dictionary<string, string[]>(); // 采样失败不阻塞主流程
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

    /// <summary>安全读取 JsonNode 字符串（缺失 / JSON null / 非字符串均返回 null）。</summary>
    public static string? NodeStr(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? TryExtractOpenAiError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?["message"]?.GetValue<string>() ?? n?["message"]?.GetValue<string>();
        }
        catch { return null; }
    }
}
