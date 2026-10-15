using EsClient.Api.Es;
using EsClient.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddSingleton<AiSettingsStore>();
builder.Services.AddSingleton<MappingService>();
builder.Services.AddSingleton<QueryBuilder>();
builder.Services.AddSingleton<PagerService>();
builder.Services.AddSingleton<ExportTaskStore>();
builder.Services.AddHostedService<ExportTaskService>();

var app = builder.Build();

// 统一异常 → JSON {error}
app.UseExceptionHandler(a => a.Run(async context =>
{
    var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (code, msg) = ex switch
    {
        EsException e => (e.StatusCode, e.Message),
        _ => (500, $"服务内部错误：{ex?.Message}")
    };
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

app.Run();
