using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using EsClient.Api.Es;
using MiniExcelLibs;

namespace EsClient.Api.Services;

/// <summary>导出任务状态。</summary>
public class ExportTaskInfo
{
    public string Id { get; init; } = "";
    public string Index { get; init; } = "";
    public string Status { get; set; } = "queued"; // queued|running|done|error|canceled
    public long RowsExported { get; set; }
    public long? Total { get; set; }
    public double? Percent { get; set; }
    public string? FileName { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public bool FileDeleted { get; set; }
    internal CancellationTokenSource Cts { get; } = new();
    public bool CancelRequested => Cts.IsCancellationRequested;
}

/// <summary>导出请求。</summary>
public record ExportRequest(
    string Index,
    QuerySpec? Query,
    List<string>? Columns,   // null = 全部列
    bool IncludeId,
    long MaxRows);

/// <summary>导出任务存储 + 提交。</summary>
public class ExportTaskStore
{
    public const long ExcelMaxRows = 1_048_575; // xlsx 单 sheet 数据行上限（减表头），本工具硬上限
    public const long DefaultMaxRows = 100_000;

    /// <summary>导出临时目录：Service 写出与 Controller 下载共用同一路径，避免两处硬编码分叉。</summary>
    public static string ExportDir => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "es-client-exports");

    private readonly ConcurrentDictionary<string, ExportTaskInfo> _tasks = new();
    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new();
    private readonly Channel<string> _queue;
    private readonly ConnectionStore _conn;
    private readonly MappingService _mapping;
    private readonly QueryBuilder _queryBuilder;

    public ExportTaskStore(ConnectionStore conn, MappingService mapping, QueryBuilder queryBuilder)
    {
        _conn = conn;
        _mapping = mapping;
        _queryBuilder = queryBuilder;
        _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });
    }

    public ChannelReader<string> Reader => _queue.Reader;

    public ExportTaskInfo? Get(string id) => _tasks.TryGetValue(id, out var t) ? t : null;

    public List<ExportTaskInfo> List() =>
        _tasks.Values.OrderByDescending(t => t.CreatedAt).ToList();

    internal bool TryTakeJob(string taskId, out ExportJob job) =>
        _jobs.TryRemove(taskId, out job!);

    /// <summary>回收已完成任务的条目（随定时清理的文件删除一起），避免 _tasks 无界增长。</summary>
    internal void RemoveTask(string id) => _tasks.TryRemove(id, out _);

    /// <summary>提交导出任务（校验 + 组装 DSL 后入队）。</summary>
    public async Task<ExportTaskInfo> SubmitAsync(ExportRequest req, CancellationToken ct)
    {
        _conn.Require();
        if (!IEsClient.IsValidIndexName(req.Index))
            throw new EsException($"非法索引名：{req.Index}", 400);

        var maxRows = req.MaxRows <= 0 ? DefaultMaxRows : req.MaxRows;
        if (maxRows > ExcelMaxRows)
            throw new EsException($"行数上限不得超过 {ExcelMaxRows:N0}（Excel 单工作表硬限）", 400);

        // 预检索引存在/状态，并基于全量列组装查询 DSL（条件可能引用未勾选的字段）
        var meta = await _mapping.GetMetaAsync(req.Index, ct);
        if (meta.Status != "open")
            throw new EsException($"索引 {req.Index} 处于关闭状态，无法导出", 400);

        List<ColumnMeta> cols;
        if (req.Columns == null || req.Columns.Count == 0)
        {
            cols = meta.Columns;
        }
        else
        {
            var map = meta.Columns.ToDictionary(c => c.Path, c => c);
            cols = new List<ColumnMeta>();
            foreach (var p in req.Columns)
            {
                if (!map.TryGetValue(p, out var c))
                    throw new EsException($"未知导出列：{p}", 400);
                cols.Add(c);
            }
        }
        var queryDsl = _queryBuilder.Build(req.Query, meta.Columns.ToDictionary(c => c.Path, c => c));

        var task = new ExportTaskInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Index = req.Index
        };
        _tasks[task.Id] = task;
        // 捕获提交时的建连参数（而非活动客户端）：worker 据此新建独立连接执行，
        // 生命周期与主连接解耦 —— 断开/重连主连接不会波及进行中的导出
        _jobs[task.Id] = new ExportJob(task.Id, req.Index, queryDsl, cols, req.IncludeId, maxRows,
            _conn.RequireActiveRequest());
        _queue.Writer.TryWrite(task.Id);
        return task;
    }

    internal record ExportJob(string Id, string Index, JsonNode? QueryDsl,
        List<ColumnMeta> Columns, bool IncludeId, long MaxRows, ConnectRequest Request);
}

/// <summary>导出后台服务：两个 worker 消费队列（并发 2），scroll 遍历 + MiniExcel 流式写出。</summary>
public class ExportTaskService : BackgroundService
{
    private const int ScrollBatchSize = 1000;
    private const string CellTruncateMark = "…[截断]";
    public const int ExcelCellMaxLength = 32767;

    private readonly ILogger<ExportTaskService> _logger;
    private readonly ExportTaskStore _store;
    private readonly ConnectionStore _conn;
    private readonly string _exportDir;

    public ExportTaskService(ILogger<ExportTaskService> logger, ExportTaskStore store,
        ConnectionStore conn, IHostEnvironment env)
    {
        _logger = logger;
        _store = store;
        _conn = conn;
        _exportDir = ExportTaskStore.ExportDir;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_exportDir);
        CleanupOnStartup();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        var workers = new Task[]
        {
            WorkerLoop(stoppingToken),
            WorkerLoop(stoppingToken),
            CleanupLoop(timer, stoppingToken)
        };
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException) { }
    }

    private async Task WorkerLoop(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var taskId in _store.Reader.ReadAllAsync(stoppingToken))
            {
                if (_store.TryTakeJob(taskId, out var job))
                    await RunJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunJobAsync(ExportTaskStore.ExportJob job, CancellationToken appStopping)
    {
        var task = _store.Get(job.Id);
        if (task == null) return;

        // 排队期间被取消
        if (task.CancelRequested)
        {
            task.Status = "canceled";
            return;
        }

        task.Status = "running";
        var file = Path.Combine(_exportDir, $"{task.Id}.xlsx");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(task.Cts.Token, appStopping);
        var ct = cts.Token;

        // 独立连接：用提交时保存的建连参数新建，导出结束即释放，
        // 与主连接生命周期解耦（断开/重连主连接不影响进行中的导出）
        EsHttpClient client;
        try
        {
            client = await EsHttpClient.CreateAsync(job.Request, ct);
        }
        catch (OperationCanceledException)
        {
            task.Status = "canceled";
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出任务 {TaskId} 建连失败", task.Id);
            task.Status = "error";
            task.Error = ex is EsException ? ex.Message : $"导出建连失败：{ex.Message}";
            return;
        }

        try
        {
            // 预检总数
            long? total;
            if (job.QueryDsl == null)
            {
                var stats = await client.GetStatsAsync(job.Index, ct);
                total = stats["_all"]?["primaries"]?["docs"]?["count"]?.GetValue<long>();
            }
            else
            {
                try
                {
                    total = await client.CountAsync(job.Index, job.QueryDsl, ct);
                }
                catch
                {
                    total = null; // 计数失败不阻塞导出，进度退化为行数播报
                }
            }
            task.Total = total;

            // 生产者：scroll 拉取批次；消费者：MiniExcel 流式写出
            var batches = new BlockingCollection<Dictionary<string, object?>[]>(200);
            var producer = ProduceScrollAsync(batches, client, job, total, task, ct);
            var rows = ConsumeRows(producer, batches, ct);

            await using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write,
                FileShare.None, 1 << 16, useAsync: true))
            {
                await MiniExcel.SaveAsAsync(stream, rows, cancellationToken: ct);
            }

            if (task.CancelRequested)
            {
                task.Status = "canceled";
                TryDelete(file);
                return;
            }
            task.Percent = 100;
            task.FileName = $"{SanitizeFileName(job.Index)}-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx";
            task.Status = "done";
        }
        catch (OperationCanceledException)
        {
            task.Status = "canceled";
            TryDelete(file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出任务 {TaskId} 失败", task.Id);
            task.Status = "error";
            task.Error = ex is EsException ? ex.Message : $"导出失败：{ex.Message}";
            TryDelete(file);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>scroll 生产者：批量拉取并转换为行字典，批次间检查取消。</summary>
    private async Task ProduceScrollAsync(BlockingCollection<Dictionary<string, object?>[]> batches,
        EsHttpClient client, ExportTaskStore.ExportJob job, long? total, ExportTaskInfo task, CancellationToken ct)
    {
        var scrollId = "";
        try
        {
            var body = new JsonObject
            {
                ["size"] = ScrollBatchSize,
                ["sort"] = new JsonArray(new JsonObject { ["_doc"] = "asc" })
            };
            if (job.QueryDsl != null) body["query"] = job.QueryDsl.DeepClone();

            var (res, sid) = await client.SearchWithScrollAsync(job.Index, body, "2m", ct);
            scrollId = sid;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var hits = res["hits"]?["hits"] as JsonArray;
                if (hits == null || hits.Count == 0) break;

                var batch = new List<Dictionary<string, object?>>(hits.Count);
                foreach (var h in hits.OfType<JsonObject>())
                {
                    if (batch.Count >= job.MaxRows - task.RowsExported) break;
                    batch.Add(BuildRow(job, h));
                }
                task.RowsExported += batch.Count;
                if (total is > 0)
                    task.Percent = Math.Min(99.9, task.RowsExported * 100.0 / Math.Min(total.Value, job.MaxRows));
                if (batch.Count > 0) batches.Add(batch.ToArray(), ct);

                if (batch.Count < hits.Count || task.RowsExported >= job.MaxRows) break; // 达行数上限

                (res, scrollId) = await client.ScrollNextAsync(scrollId, "2m", ct);
            }
        }
        finally
        {
            // 任何退出路径都释放 scroll 上下文
            if (!string.IsNullOrEmpty(scrollId))
            {
                try { await client.ClearScrollAsync(scrollId); }
                catch { /* 尽力释放 */ }
            }
            batches.CompleteAdding();
        }
    }

    private static Dictionary<string, object?> BuildRow(ExportTaskStore.ExportJob job, JsonObject hit)
    {
        var row = new Dictionary<string, object?>(job.Columns.Count + 1);
        if (job.IncludeId) row["_id"] = hit["_id"]?.GetValue<string>();
        var source = hit["_source"] as JsonObject;
        foreach (var c in job.Columns)
            row[c.Path] = ExtractExportValue(source, c.Path);
        return row;
    }

    /// <summary>消费者：同步枚举批次喂给 MiniExcel；枚举结束后传播生产者异常。</summary>
    private static IEnumerable<Dictionary<string, object?>> ConsumeRows(
        Task producer, BlockingCollection<Dictionary<string, object?>[]> batches, CancellationToken ct)
    {
        foreach (var batch in batches.GetConsumingEnumerable(ct))
            foreach (var row in batch)
                yield return row;

        try
        {
            producer.Wait();
        }
        catch (AggregateException ex)
        {
            throw ex.InnerExceptions.Count > 0 ? ex.InnerExceptions[0] : ex;
        }
    }

    /// <summary>导出值转换：数组/对象 → JSON；超长字符串截断（Excel 单格 32767 硬限）。</summary>
    internal static object? ExtractExportValue(JsonObject? source, string path)
    {
        JsonNode? cur = source;
        var segments = path.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            if (cur is not JsonObject obj) return null;
            cur = obj[segments[i]];
            if (cur == null) return null;
            if (cur is JsonArray) break;
        }

        switch (cur)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return s.Length <= ExcelCellMaxLength
                    ? s
                    : s[..(ExcelCellMaxLength - CellTruncateMark.Length)] + CellTruncateMark;
            case JsonValue n when n.TryGetValue<decimal>(out var d):
                return d; // 数值列保持数值类型
            case JsonValue b when b.TryGetValue<bool>(out var bv):
                return bv ? "true" : "false";
            case JsonObject or JsonArray:
                return cur.ToJsonString();
            case JsonValue nv:
                return nv.ToJsonString();
            default:
                return null;
        }
    }

    private static string SanitizeFileName(string name) =>
        string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch { /* 忽略 */ }
    }

    /// <summary>启动时清理 24h 前的残留文件。</summary>
    private void CleanupOnStartup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_exportDir))
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-24))
                    TryDelete(f);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动清理导出目录失败");
        }
    }

    /// <summary>定时清理：已完成任务文件保留 1 小时。</summary>
    private async Task CleanupLoop(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                foreach (var t in _store.List())
                {
                    if (t.Status is "done" or "error" or "canceled" &&
                        DateTime.Now - t.CreatedAt > TimeSpan.FromHours(1))
                    {
                        TryDelete(Path.Combine(_exportDir, $"{t.Id}.xlsx"));
                        // 条目随文件一起回收：否则 _tasks 只增不减，长驻进程稳定泄漏。
                        // 前端只轮询单个状态并即时下载，列表端点无消费方，移除条目不影响 UI。
                        _store.RemoveTask(t.Id);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
