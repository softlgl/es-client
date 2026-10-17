using EsClient.Api.Es;
using EsClient.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
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

// API 请求日志：方法、路径、状态码、耗时（静态资源不记录，避免噪音）
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(); return; }
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();
    context.RequestServices.GetRequiredService<ILogger<Program>>()
        .LogInformation("{Method} {Path} → {StatusCode} ({Elapsed:F0} ms)",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, sw.Elapsed.TotalMilliseconds);
});

app.UseDefaultFiles();
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        // 禁止任何缓存：前端迭代频繁，旧脚本会导致行为与源码不一致
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate"
    });
app.MapControllers();

app.Run();
