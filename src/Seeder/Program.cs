using System.Net.Http.Headers;
using System.Text;

// 造数工具：dotnet run --project src/Seeder -- --host http://localhost:9200 --count 120000
// 创建三个索引：orders（显式 mapping）、weird（脏数据）、empty（空索引）

var host = "http://localhost:9200";
var count = 120_000L;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--host") host = args[i + 1].TrimEnd('/');
    if (args[i] == "--count" && long.TryParse(args[i + 1], out var c)) count = c;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.BaseAddress = new Uri(host + "/");

async Task<string> SendAsync(HttpMethod m, string path, string? body = null, string? contentType = null)
{
    using var req = new HttpRequestMessage(m, path);
    if (body != null) req.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    if (!res.IsSuccessStatusCode && !(m == HttpMethod.Delete && (int)res.StatusCode == 404))
        throw new Exception($"{m} {path} → {(int)res.StatusCode}: {text}");
    return text;
}

Console.WriteLine($"目标：{host}");

// ---------- orders ----------
Console.WriteLine("删除并创建 orders …");
await SendAsync(HttpMethod.Delete, "orders");
var mapping = """
{
  "mappings": {
    "properties": {
      "order_no":   { "type": "keyword" },
      "amount":     { "type": "double" },
      "status":     { "type": "keyword" },
      "created_at": { "type": "date" },
      "remark":     { "type": "text", "fields": { "keyword": { "type": "keyword", "ignore_above": 256 } } },
      "user": {
        "properties": {
          "name": { "type": "keyword" },
          "age":  { "type": "long" }
        }
      },
      "tags": { "type": "keyword" }
    }
  },
  "settings": { "number_of_shards": 1, "number_of_replicas": 0 }
}
""";
await SendAsync(HttpMethod.Put, "orders", mapping);

var statuses = new[] { "paid", "pending", "shipped", "canceled", "refunded" };
var rnd = new Random(42);
var sw = System.Diagnostics.Stopwatch.StartNew();

async Task BulkAsync(List<string> docs, string index)
{
    var sb = new StringBuilder();
    foreach (var d in docs)
        sb.Append($"{{\"index\":{{\"_index\":\"{index}\"}}}}\n{d}\n");
    var res = await SendAsync(HttpMethod.Post, "_bulk", sb.ToString(), "application/x-ndjson");
    using var doc = System.Text.Json.JsonDocument.Parse(res);
    if (doc.RootElement.TryGetProperty("errors", out var e) && e.GetBoolean())
        throw new Exception("bulk 写入存在错误项：" + res[..500]);
}

var batch = new List<string>(1000);
for (long i = 1; i <= count; i++)
{
    var status = statuses[rnd.Next(statuses.Length)];
    var remark = i % 50 == 0
        ? string.Concat(Enumerable.Repeat("超长备注内容测试。", 40))
        : $"订单备注 {i}：模拟数据";
    var doc = $$"""
{
  "order_no": "ORD-{{i:D8}}",
  "amount": {{Math.Round(rnd.NextDouble() * 1000, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)}},
  "status": "{{status}}",
  "created_at": "{{DateTime.UtcNow.AddDays(-rnd.Next(365)):yyyy-MM-ddTHH:mm:ss.fffZ}}",
  "remark": "{{remark}}",
  "user": { "name": "user-{{i % 1000:D4}}", "age": {{18 + i % 50}} },
  "tags": ["t{{i % 5}}", "t{{i % 7}}"]
}
""";
    batch.Add(doc.ReplaceLineEndings(" ").Replace("\r", "").Replace("\n", "").Replace("  ", " "));
    if (batch.Count >= 1000)
    {
        await BulkAsync(batch, "orders");
        batch.Clear();
        if (i % 10000 == 0)
            Console.WriteLine($"  已写入 {i:N0} / {count:N0}，用时 {sw.Elapsed.TotalSeconds:F1}s");
    }
}
if (batch.Count > 0) await BulkAsync(batch, "orders");
Console.WriteLine($"orders 完成：{count:N0} 条，用时 {sw.Elapsed.TotalSeconds:F1}s");

// ---------- weird ----------
Console.WriteLine("创建 weird（脏数据边界用例）…");
await SendAsync(HttpMethod.Delete, "weird");
var longText = new string('长', 40000);
var deep = string.Join("", Enumerable.Range(1, 10).Select(i => $"{{\"lvl{i}\":"));
deep += "\"底部\"";
deep += string.Join("", Enumerable.Range(1, 10).Select(i => "}"));
var weirdDocs = new List<string>
{
    $$"""{"title":"超长文本","content":"{{longText}}"}""",
    "{\"title\":\"深嵌套与数组\",\"deep\":" + deep + ",\"items\":[{\"k\":1,\"v\":\"a\"},{\"k\":2,\"v\":\"b\"}],\"arr\":[1,2,3]}",
    """{"title":"空对象","empty_obj":{},"nil":null}"""
};
await BulkAsync(weirdDocs, "weird");

// ---------- empty ----------
Console.WriteLine("创建 empty（空索引）…");
await SendAsync(HttpMethod.Delete, "empty");
await SendAsync(HttpMethod.Put, "empty", """{"settings":{"number_of_shards":1,"number_of_replicas":0}}""");

// 刷新确保可见
await SendAsync(HttpMethod.Post, "_refresh");
Console.WriteLine("全部完成。");
