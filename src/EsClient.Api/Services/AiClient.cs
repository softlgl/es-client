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
    /// 以 stream 模式调用上游接口，逐块回调 onEvent("reasoning"/"content", text)，返回完整 content。
    /// thinking 为 true 时优先使用配置的思考模型。
    /// </summary>
    public async Task<string> StreamAsync(List<object> messages, bool thinking,
        Func<string, string, Task>? onEvent, CancellationToken ct)
    {
        var s = ai.Get();
        var http = httpFactory.CreateClient("ai");
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
