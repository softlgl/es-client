using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace EsClient.Api.Services.Chat;

/// <summary>
/// MEAI 抽象类型 ⇄ OpenAI Chat Completions 线上格式的映射层（协议层，纯函数）。
/// 拆出来有两个考量：与 connector 的 HTTP/SSE 传输解耦后可靠编译期检查逐段验证；
/// 国产端点的兼容细节（如 assistant 回填须带 reasoning_content，DeepSeek 思考+工具
/// 多轮缺它报 400）都集中在这一份映射里，改协议只动一处。
/// </summary>
internal static class OpenAiWireFormat
{
    /// <summary>ChatOptions.Tools 里的 AITool → OpenAI tools 数组（function 型）。</summary>
    public static JsonArray ToolsArray(IList<AITool>? tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools ?? [])
        {
            var fn = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description ?? "",
            };
            // AIFunction 才有参数 schema（JsonSchema 为 JsonElement）；纯 ChatTool 只下发名字
            if (t is AIFunction f && f.JsonSchema.ValueKind == JsonValueKind.Object)
            {
                try { fn["parameters"] = JsonNode.Parse(f.JsonSchema.GetRawText()); }
                catch { /* schema 异常时按无参下发，端点会报参数校验错而非静默吞掉 */ }
            }
            arr.Add(new JsonObject { ["type"] = "function", ["function"] = fn });
        }
        return arr;
    }

    /// <summary>
    /// ChatMessage → OpenAI messages 元素。覆盖四种形态：
    /// 普通角色 / assistant 带 tool_calls（须同时带回 reasoning_content）/
    /// FunctionResultContent 转成的 role:"tool" 消息 / 免序列化捷径（纯文本）。
    /// </summary>
    public static JsonArray MessagesArray(IList<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var calls = m.Contents.OfType<FunctionCallContent>().ToList();

            // role:"tool" —— 工具结果回填消息（MEAI 把结果挂在 assistant 角色的 FunctionResultContent 里，
            // 线上格式要求独立成 tool 消息，按 OpenAI 协议逐个展开）
            if (m.Role != ChatRole.System && m.Contents.Any(c => c is FunctionResultContent))
            {
                foreach (var r in m.Contents.OfType<FunctionResultContent>())
                {
                    var result = r.Exception != null ? $"工具执行失败：{r.Exception.Message}"
                        : r.Result switch
                        {
                            null => "",
                            string s => s,                 // 本项目工具统一返回 JSON 字符串，直接透传
                            JsonNode j => j.ToJsonString(),
                            var o => System.Text.Json.JsonSerializer.Serialize(o)
                        };
                    arr.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId ?? "", ["content"] = result });
                }
                // 同消息里若还带文本（个别端点会这么回），一并补一条 assistant 消息避免丢失
                if (!string.IsNullOrEmpty(m.Text))
                    arr.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Text });
                continue;
            }

            var msg = new JsonObject { ["role"] = m.Role.Value };
            // 纯工具调用轮 content 为空：按 OpenAI 惯例给 null（qwen 系对 null 容忍、对缺字段报错的未见，保持原样更稳）
            msg["content"] = calls.Count > 0 && string.IsNullOrEmpty(m.Text) ? null : JsonValue.Create(m.Text ?? "");

            // 思考模型多轮回填：reasoning_content 只在 assistant 带 tool_calls 时强制携带（DeepSeek 400 坑），
            // 其余历史按现行为只回 content，避免把无关字段广播给所有端点
            var reasoning = m.Contents.OfType<TextReasoningContent>().FirstOrDefault()?.Text;
            if (m.Role == ChatRole.Assistant && calls.Count > 0 && !string.IsNullOrEmpty(reasoning))
                msg["reasoning_content"] = reasoning;

            if (calls.Count > 0)
            {
                var tc = new JsonArray();
                foreach (var c in calls)
                {
                    var args = new JsonObject();
                    if (c.Arguments != null)
                        foreach (var (k, v) in c.Arguments)
                            args[k] = v is null ? null : JsonValue.Create(v.ToString());
                    tc.Add(new JsonObject
                    {
                        ["id"] = c.CallId ?? "",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = args.ToJsonString() }
                    });
                }
                msg["tool_calls"] = tc;
            }
            arr.Add(msg);
        }
        return arr;
    }

    /// <summary>
    /// 流式 delta.tool_calls 的按 index 归并器：id/name 首块给全，arguments 按 token 分片
    /// （DeepSeek parallel_tool_calls=false 时分片可能是非法 JSON 片段，故只累积字符串、
    /// 流结束统一解析）。每个 HTTP 请求对应一个新实例。
    /// </summary>
    public sealed class ToolCallAggregator
    {
        private sealed class Entry
        {
            public string? Id;
            public string? Name;
            public readonly System.Text.StringBuilder Args = new();
        }

        private readonly Dictionary<int, Entry> _byIndex = [];

        /// <summary>把一个 chunk 的 delta.tool_calls 数组合并进累积区。</summary>
        public void Feed(JsonArray? deltas)
        {
            if (deltas == null) return;
            foreach (var node in deltas)
            {
                if (node is not JsonObject d) continue;
                var idx = d["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 0;
                if (!_byIndex.TryGetValue(idx, out var entry))
                    _byIndex[idx] = entry = new Entry();
                var id = Str(d["id"]); var name = Str(d["function"]?["name"]); var arg = Str(d["function"]?["arguments"]);
                // id/name 首块给全，但部分端点会把 name 拆在后续块里，故空则补写
                if (!string.IsNullOrEmpty(id)) entry.Id ??= id;
                if (!string.IsNullOrEmpty(name)) entry.Name += name;
                if (!string.IsNullOrEmpty(arg)) entry.Args.Append(arg);
            }
        }

        /// <summary>是否累积到了任何工具调用。</summary>
        public bool HasCalls => _byIndex.Count > 0;

        /// <summary>流结束后产出最终的 FunctionCallContent 列表（arguments 解析失败按空参处理并保留原始串供排查）。</summary>
        public List<FunctionCallContent> BuildCalls()
        {
            var list = new List<FunctionCallContent>();
            foreach (var (_, entry) in _byIndex.OrderBy(kvp => kvp.Key))
            {
                Dictionary<string, object?> args = new(StringComparer.Ordinal);
                var raw = entry.Args.ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        if (JsonNode.Parse(raw) is JsonObject obj)
                            foreach (var (k, v) in obj)
                                args[k] = v switch
                                {
                                    JsonValue jv when jv.TryGetValue<string>(out var s) => s,
                                    JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
                                    JsonValue jv when jv.TryGetValue<long>(out var l) => l,
                                    JsonValue jv when jv.TryGetValue<double>(out var dd) => dd,
                                    null => null,
                                    var other => other.ToJsonString()
                                };
                    }
                    catch { /* 非法 JSON 片段：留空参数，工具校验会报「缺少参数」喂回模型自愈 */ }
                }
                list.Add(new FunctionCallContent(entry.Id ?? "", entry.Name ?? "", args));
            }
            return list;
        }

        private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
