using System.Text.Json.Nodes;
using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsClient.Api.Controllers;

[ApiController]
public class DocumentsController : ControllerBase
{
    private readonly PagerService _pager;
    private readonly ConnectionStore _store;

    public DocumentsController(PagerService pager, ConnectionStore store)
    {
        _pager = pager;
        _store = store;
    }

    public record NewDocRequest(string? Id, JsonObject Doc);

    /// <summary>新增文档（id 可选，留空自动生成）。</summary>
    [HttpPost("/api/indices/{name}/docs")]
    public async Task<IActionResult> CreateDoc(string name, [FromBody] NewDocRequest req, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var res = await _store.Require()
            .IndexDocAsync(name, string.IsNullOrWhiteSpace(req.Id) ? null : req.Id.Trim(), req.Doc, ct);
        return Ok(new
        {
            id = res?["_id"]?.GetValue<string>(),
            result = res?["result"]?.GetValue<string>()
        });
    }

    /// <summary>全量替换文档。</summary>
    [HttpPut("/api/indices/{name}/documents/{*id}")]
    public async Task<IActionResult> UpdateDoc(string name, string id, [FromBody] JsonObject doc, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var res = await _store.Require().PutDocAsync(name, id, doc, ct);
        return Ok(new { id = res?["_id"]?.GetValue<string>(), result = res?["result"]?.GetValue<string>() });
    }

    /// <summary>删除文档。</summary>
    [HttpDelete("/api/indices/{name}/documents/{*id}")]
    public async Task<IActionResult> DeleteDoc(string name, string id, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var res = await _store.Require().DeleteDocAsync(name, id, ct);
        return Ok(new { id = res?["_id"]?.GetValue<string>(), result = res?["result"]?.GetValue<string>() });
    }

    [HttpPost("/api/indices/{name}/documents")]
    public async Task<IActionResult> Documents(string name, [FromBody] BrowseRequest req, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var res = await _pager.BrowseAsync(name, req, ct);
        return Ok(new
        {
            rows = res.Rows.Select(r => new
            {
                id = r.Id,
                cells = r.Cells.Select(c => new { v = c.V, kind = c.Kind }),
                after = r.Sort
            }),
            total = res.Total,
            mode = res.Mode,
            from = res.From,
            nextAfter = res.NextAfter
        });
    }

    [HttpPost("/api/indices/{name}/count")]
    public async Task<IActionResult> Count(string name, [FromBody] QuerySpec? query, CancellationToken ct)
    {
        if (!IEsClient.IsValidIndexName(name))
            return BadRequest(new { error = $"非法索引名：{name}" });
        var count = await _pager.CountAsync(name, query, ct);
        return Ok(new { count });
    }
}
