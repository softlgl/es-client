using System.Text.Json.Nodes;
using EsClient.Api.Es;
using Microsoft.Extensions.AI;

namespace EsClient.Api.Services;

/// <summary>
/// 只读工具集：为 AI 问答的「分步取数」回路提供模型可调用的函数。
/// 全部包一层现有 IEsClient 只读方法，DSL 进 execute 前一律过 <see cref="AiDslGuard"/>
/// 同款护栏（白名单、禁 script、限 size、钳 terms）——与主问答链路同等安全等级。
/// 写入类能力（增删改文档/索引）根本不注册，模型无从调用，这比运行时拦截更干净。
/// 工具实例每请求构建：闭包捕获当次的 EsHttpClient 与 index，index 不作为入参暴露，
/// 天然锁定当前会话索引，无需再做跨索引权限判断。
/// </summary>
public static class AiTools
{
    /// <summary>构建绑定到 (client, index, meta) 的只读工具列表。</summary>
    public static List<AITool> Build(EsHttpClient client, string index, IndexMeta meta, CancellationToken ct)
    {
        return
        [
            AIFunctionFactory.Create(RunDslAsync, name: "run_dsl",
                description: "对当前索引执行一次临时 search 请求并返回聚合/命中结果，用于分步验证口径或中间取数。" +
                             "参数 dsl 为一个 JSON 对象字符串，顶层可用键：query、aggs、size、sort、_source。禁止 script。"),
            AIFunctionFactory.Create(CountDocsAsync, name: "count_docs",
                description: "统计当前索引中满足 query 的文档总数（不取回文档，成本低）。参数 query 为单个 query 子句的 JSON 字符串。"),
            AIFunctionFactory.Create(SampleFieldAsync, name: "sample_field_values",
                description: "采样某字段的真实取值（keyword/terms 取高频值，数值/日期取区间），用于确认字段是否有序号/拼写/可选值。参数 field 为字段完整路径。"),
        ];

        // —— 工具实现（局部函数，参数名会进 JSON Schema 供模型填参）——

        async Task<string> RunDslAsync(string dsl)
        {
            var (body, err) = AiDslGuard.ExtractSearchBody(dsl ?? "");
            if (body == null) return ErrorJson(err ?? "DSL 解析失败");
            try
            {
                var res = await client.SearchAsync(index, body, ct);
                // 与主问答链路共用同一份裁剪：桶 ≤15、明细行 ≤8、单元格 ≤100，并保留 terms 截断的 _note 告警
                return AiResultProjector.TrimForModel(res, bucketCap: 15, rowCap: 8, cellMax: 100).ToJsonString();
            }
            catch (EsException ex) { return ErrorJson(ex.Message); }
        }

        async Task<string> CountDocsAsync(string query)
        {
            var (node, err) = AiDslGuard.TryParseObject(query ?? "");
            if (node == null) return ErrorJson(err ?? "query 解析失败");
            if (AiDslGuard.HasScriptKey(node)) return ErrorJson("不支持 script");
            try
            {
                // 允许传 {"query":{...}} 或裸 query 子句
                var q = node["query"] as JsonObject ?? node;
                var total = await client.CountAsync(index, q, ct);
                return new JsonObject { ["total"] = total }.ToJsonString();
            }
            catch (EsException ex) { return ErrorJson(ex.Message); }
        }

        async Task<string> SampleFieldAsync(string field)
        {
            var col = meta.Columns.FirstOrDefault(c =>
                string.Equals(c.Path, field, StringComparison.OrdinalIgnoreCase));
            var aggField = col?.KeywordPath ?? field;
            // 数值/日期走 stats 取区间，其余走 terms 取高频值——类型清单与采样、AI 生成共用一份，避免手抄漂移
            var isNumeric = col != null && (ColumnMeta.NumericTypes.Contains(col.Type) || ColumnMeta.DateTypes.Contains(col.Type));
            var agg = isNumeric
                ? new JsonObject { ["stats"] = new JsonObject { ["field"] = field } }
                : new JsonObject { ["terms"] = new JsonObject { ["field"] = aggField, ["size"] = 8 } };
            try
            {
                var res = await client.SearchAsync(index,
                    new JsonObject { ["size"] = 0, ["aggs"] = new JsonObject { ["a0"] = agg } }, ct);
                var a0 = res?["aggregations"]?["a0"];
                if (isNumeric)
                    return new JsonObject
                    {
                        ["min"] = a0?["min"]?.DeepClone(), ["max"] = a0?["max"]?.DeepClone(),
                        ["avg"] = a0?["avg"]?.DeepClone(), ["count"] = a0?["count"]?.DeepClone()
                    }.ToJsonString();
                var buckets = a0?["buckets"] as JsonArray;
                var values = buckets?.Select(b => b?["key"]?.DeepClone() ?? (JsonNode)"").ToArray() ?? [];
                return new JsonObject { ["values"] = new JsonArray(values) }.ToJsonString();
            }
            catch (EsException ex) { return ErrorJson(ex.Message); }
        }
    }

    private static string ErrorJson(string msg) => new JsonObject { ["error"] = msg }.ToJsonString();

    /// <summary>
    /// 工具名 → 前端展示用中文名。与 <see cref="Build"/> 里各 AIFunctionFactory.Create 的 name: 一一对应，
    /// 集中到工具定义同处，避免改工具名时漏改展示表（此前该映射埋在 ToolLoopChatClient 中间件里，
    /// 工具名与其展示名两处维护易漂移）。
    /// </summary>
    public static string DisplayName(string name) => name switch
    {
        "run_dsl" => "执行临时查询",
        "count_docs" => "统计命中数",
        "sample_field_values" => "采样字段值",
        _ => name
    };
}
