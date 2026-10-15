using System.Text.Json;
using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsClient.Api.Controllers;

[ApiController]
public class IndicesController : ControllerBase
{
    private readonly ConnectionStore _store;
    private readonly MappingService _mapping;

    public IndicesController(ConnectionStore store, MappingService mapping)
    {
        _store = store;
        _mapping = mapping;
    }

    public record IndexSummary(string Index, string Health, string Status, long? DocsCount, long? StoreSize, bool System);

    [HttpGet("/api/indices")]
    public async Task<IActionResult> Indices(CancellationToken ct)
    {
        var arr = await _store.Require().CatIndicesAsync(ct);
        var list = new List<IndexSummary>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject o) continue;
            var name = o["index"]?.GetValue<string>() ?? "";
            long? docs = long.TryParse(o["docs.count"]?.GetValue<string>(), out var d) ? d : null;
            long? size = long.TryParse(o["store.size"]?.GetValue<string>(), out var s) ? s : null;
            list.Add(new IndexSummary(
                name,
                o["health"]?.GetValue<string>() ?? "unknown",
                o["status"]?.GetValue<string>() ?? "open",
                docs, size,
                name.StartsWith('.')));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Index, b.Index));
        return Ok(list);
    }

    [HttpGet("/api/indices/{name}/meta")]
    public async Task<IActionResult> Meta(string name, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var meta = await _mapping.GetMetaAsync(name, ct);
        return Ok(new
        {
            index = meta.Index,
            status = meta.Status,
            health = meta.Health,
            docsCount = meta.DocsCount,
            maxResultWindow = meta.MaxResultWindow,
            columns = meta.Columns.Select(c => new
            {
                path = c.Path,
                type = c.Type,
                sortable = c.Sortable,
                keywordPath = c.KeywordPath
            })
        });
    }

    [HttpDelete("/api/indices/{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        await _store.Require().DeleteIndexAsync(name, ct);
        _mapping.Invalidate(name); // 清除元数据缓存，避免重建同名索引后列定义过期
        return Ok(new { deleted = true });
    }

    [HttpGet("/api/indices/{name}/mapping")]
    public async Task<IActionResult> Mapping(string name, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var mapping = await _store.Require().GetMappingAsync(name, ct);
        return Content(mapping.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }), "application/json");
    }

    [HttpGet("/api/indices/{name}/documents/{*id}")]
    public async Task<IActionResult> Document(string name, string id, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var doc = await _store.Require().GetDocumentAsync(name, id, ct);
        var found = doc["found"]?.GetValue<bool>() ?? false;
        if (!found) return NotFound(new { error = "文档不存在" });
        return Content(doc["_source"]!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        }), "application/json");
    }
}
