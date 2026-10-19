using EsClient.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsClient.Api.Controllers;

[ApiController]
public class ExportController : ControllerBase
{
    private readonly ExportTaskStore _store;
    private readonly ILogger<ExportController> _logger;

    public ExportController(ExportTaskStore store, ILogger<ExportController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpPost("/api/export")]
    public async Task<IActionResult> Submit([FromBody] ExportRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Index))
            return BadRequest(new { error = "索引名不能为空" });
        var task = await _store.SubmitAsync(req, ct);
        return Ok(new { taskId = task.Id });
    }

    [HttpGet("/api/export/tasks")]
    public IActionResult Tasks() =>
        Ok(_store.List().Select(t => new
        {
            id = t.Id,
            index = t.Index,
            status = t.Status,
            rowsExported = t.RowsExported,
            total = t.Total,
            percent = t.Percent,
            fileName = t.FileName,
            error = t.Error,
            createdAt = t.CreatedAt,
            fileDeleted = t.FileDeleted
        }));

    [HttpGet("/api/export/{id}")]
    public IActionResult Status(string id)
    {
        var t = _store.Get(id);
        if (t == null) return NotFound(new { error = "任务不存在" });
        return Ok(new
        {
            id = t.Id,
            index = t.Index,
            status = t.Status,
            rowsExported = t.RowsExported,
            total = t.Total,
            percent = t.Percent,
            fileName = t.FileName,
            error = t.Error,
            fileDeleted = t.FileDeleted
        });
    }

    [HttpPost("/api/export/{id}/cancel")]
    public IActionResult Cancel(string id)
    {
        var t = _store.Get(id);
        if (t == null) return NotFound(new { error = "任务不存在" });
        if (t.Status is "queued" or "running")
            t.Cts.Cancel();
        return Ok(new { ok = true });
    }

    [HttpGet("/api/export/{id}/download")]
    public IActionResult Download(string id)
    {
        var t = _store.Get(id);
        if (t == null) return NotFound(new { error = "任务不存在" });
        if (t.Status != "done")
            return BadRequest(new { error = $"任务尚未完成（{t.Status}）" });
        var file = Path.Combine(ExportTaskStore.ExportDir, $"{t.Id}.xlsx");
        if (!System.IO.File.Exists(file))
            return NotFound(new { error = "导出文件已过期或已被删除" });

        var fileName = t.FileName ?? $"{t.Index}.xlsx";
        Response.OnCompleted(() =>
        {
            try { System.IO.File.Delete(file); t.FileDeleted = true; }
            catch (Exception ex) { _logger.LogWarning(ex, "下载后删除临时文件失败：{File}", file); }
            return Task.CompletedTask;
        });
        return PhysicalFile(file, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }
}
