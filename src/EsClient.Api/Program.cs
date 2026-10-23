using EsClient.Api.Es;
using EsClient.Api.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
// AI 流式读的命名客户端：超时设无限，读流总时长由 AiClient 的 linked CTS（4 分钟）兜底。
// 不在 connector 里 per-call 改 Timeout —— IHttpClientFactory 返回同一份可配置实例，并发写会抛或互覆盖。
builder.Services.AddHttpClient("ai", c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton<AiSettingsStore>();
builder.Services.AddSingleton<AiClient>();
builder.Services.AddSingleton<ChatStore>();
builder.Services.AddSingleton<MappingService>();
builder.Services.AddSingleton<QueryBuilder>();
builder.Services.AddSingleton<PagerService>();
builder.Services.AddSingleton<ExportTaskStore>();
builder.Services.AddHostedService<ExportTaskService>();

var app = builder.Build();

// API 请求日志：方法、路径、状态码、耗时（静态资源不记录，避免噪音）。
// 必须注册在 UseExceptionHandler 之外（之前），成为最外层：异常在下游被处理器转成响应后正常返回，
// 这里的 await next() 不会抛出，失败的请求（500）也能记上状态码与耗时——否则日志中间件被异常穿透、这行会跳过。
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    context.RequestServices.GetRequiredService<ILogger<Program>>()
        .LogInformation("{Method} {Path} → {StatusCode} ({Elapsed:F0} ms)",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.Elapsed.TotalMilliseconds);
});

// 统一异常 → JSON {error}，同时记录日志（ES 侧错误记 Warning，未知异常记 Error）
app.UseExceptionHandler(a => a.Run(async context =>
{
    var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    var (code, msg) = ex switch
    {
        EsException e => (e.StatusCode, e.Message),
        _ => (500, $"服务内部错误：{ex?.Message}")
    };
    if (ex is EsException es)
        logger.LogWarning("ES 错误 {Method} {Path} → {Status}: {Message}", context.Request.Method, context.Request.Path, code, es.Message);
    else
        logger.LogError(ex, "请求 {Method} {Path} 处理失败", context.Request.Method, context.Request.Path);
    context.Response.StatusCode = code;
    await context.Response.WriteAsJsonAsync(new { error = msg });
}));

app.UseDefaultFiles();
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        // 禁止任何缓存：前端迭代频繁，旧脚本会导致行为与源码不一致
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate"
    });
app.MapControllers();

// 回环监听守卫：本工具的 Web API 不含鉴权，且集群凭据明文保存在 App_Data。
// 只应绑定本机回环地址；若检测到非回环（0.0.0.0 / :: / 可路由 IP）监听则告警。
// 不阻断启动（避免破坏既有部署），确需远程访问可用 ESCLIENT_ALLOW_REMOTE=1 确认知悉后降级。
app.Lifetime.ApplicationStarted.Register(() =>
{
    var addrs = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;
    if (addrs is null) return;

    static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.IsLoopback || (u.Host.Length >= 4 && u.Host.StartsWith("127.")));

    var remote = addrs.Where(a => !IsLoopback(a)).ToArray();
    if (remote.Length == 0) return;

    var ack = Environment.GetEnvironmentVariable("ESCLIENT_ALLOW_REMOTE") == "1";
    var msg = "检测到非回环监听地址：" + string.Join(", ", remote) +
        "。本工具的 API 无鉴权、凭据明文落盘，对外暴露会使任何能访问该端口者" +
        "读取凭据并操作你的 Elasticsearch。" +
        (ack ? "" : "（确需远程访问请自行评估风险，或设置环境变量 ESCLIENT_ALLOW_REMOTE=1 消除此警告）");

    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    if (ack) logger.LogInformation("远程监听已确认：{Message}", msg);
    else logger.LogWarning("{Message}", msg);
});

app.Run();
