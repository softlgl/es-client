using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EsClient.Api.Services;

/// <summary>
/// 展示用文本净化：把 JSON/Unicode 转义还原成人类可读文本。处理两类来源——
/// ① 结构叠层：值为内嵌 JSON 字符串的字段（"dsl":"{\"query\":…}"、模型双重编码的 "{\"dsl\":…}"、
///    源文档存的 JSON 字符串），逐层解析并继续解（受 maxDepth 约束）；
/// ② 字符转义：裸露的 \uXXXX 序列（\u0022=" 、\u002B=+ 、\u8FDB=進），常见于源数据用
///    .NET 默认序列化写入，或我们自己 ToJsonString 的默认编码器输出。
/// 输出统一走宽松编码器（见 <see cref="Out"/>），中文与 + " 等符号不再被转成 \uXXXX。
/// 只用于 status/日志/表格等展示面；回填给模型的消息历史必须保持原样合法 JSON，勿调用。
/// 解不出（非法 JSON、超深、普通文本）一律原样返回。
/// </summary>
internal static class JsonDisplayUnwrapper
{
    // 宽松编码器：本类内部再序列化那一下不引入 \uXXXX（传输层 SSE 的对称转义由前端 JSON.parse 还原，不在此处理）。
    internal static readonly JsonSerializerOptions Out = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 只匹配 \uXXXX 形式的 Unicode 转义，不碰其它反斜杠（避免误伤 Windows 路径里的 \f、\t 等）。
    private static readonly Regex UnicodeEscape = new("\\\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    public static string Unwrap(string? s, int maxDepth = 6) =>
        string.IsNullOrEmpty(s) ? s ?? "" : UnwrapText(s, maxDepth);

    /// <summary>把一段文本按「可能是编码 JSON」逐层解开，返回最终展示文本（对象/数组返回其 JSON，标量返回纯文本）。</summary>
    private static string UnwrapText(string s, int depth)
    {
        var node = TryParseJson(s);
        if (node is null) return DecodeUnicode(s); // 整段非 JSON：只解开裸露的 \uXXXX
        if (node is JsonValue v && v.TryGetValue<string>(out var inner))
            return UnwrapText(inner, depth - 1);  // 带引号标量：可能又裹一层，继续解
        if (depth <= 0) return DecodeUnicode(s);
        try { return UnwrapNode(node, depth - 1)?.ToJsonString(Out) ?? DecodeUnicode(s); }
        catch (InvalidOperationException) { return s; } // 节点形态异常时放弃净化，保原文
    }

    /// <summary>仅当文本形如 JSON 值（对象/数组/带引号标量）才解析，纯中文/数字文本直接判非 JSON，避免误解析。</summary>
    private static JsonNode? TryParseJson(string s)
    {
        var t = s.Trim();
        if (t.Length < 1) return null;
        char c = t[0];
        if (c != '{' && c != '[' && c != '"') return null;
        try { return JsonNode.Parse(t); } catch (JsonException) { return null; }
    }

    /// <summary>深度重建：每个容器都是新节点，不搬移原树（JsonNode 单父约束，见 DeepClone 教训）。</summary>
    private static JsonNode? UnwrapNode(JsonNode? n, int depth)
    {
        if (depth <= 0) return n?.DeepClone();
        switch (n)
        {
            case JsonObject o:
                var jo = new JsonObject();
                foreach (var (k, v) in o) jo[k] = UnwrapNode(v, depth - 1);
                return jo;
            case JsonArray a:
                var ja = new JsonArray();
                foreach (var item in a) ja.Add(UnwrapNode(item, depth - 1));
                return ja;
            case JsonValue v when v.TryGetValue<string>(out var str):
                // 字符串值可能是编码 JSON：对象/数组解析后作为节点内嵌继续解；标量解成纯文本
                var inner = TryParseJson(str);
                if (inner is null) return JsonValue.Create(DecodeUnicode(str));
                if (inner is JsonValue iv && iv.TryGetValue<string>(out var isv))
                    return JsonValue.Create(UnwrapText(isv, depth - 1));
                return UnwrapNode(inner, depth - 1);
            default:
                return n?.DeepClone();
        }
    }

    /// <summary>把裸露的 \uXXXX 还原成字符；无反斜杠直接返回，不动其它转义形式。</summary>
    private static string DecodeUnicode(string s) =>
        s.IndexOf('\\') < 0 ? s : UnicodeEscape.Replace(s, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}
