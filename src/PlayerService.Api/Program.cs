using Microsoft.AspNetCore.Http.Timeouts;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using PlayerService.Api.Endpoints;
using PlayerService.Application;
using PlayerService.Application.Abstractions;
using PlayerService.Infrastructure.Redis;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSection("Redis").Get<RedisSettings>() ?? new();
settings.Validate();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.ToConfiguration()));
builder.Services.AddSingleton<RedisPlayerStore>();
builder.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<RedisPlayerStore>());
builder.Services.AddSingleton<IScoreUpdateStore>(sp => sp.GetRequiredService<RedisPlayerStore>());
builder.Services.AddSingleton<IGiftStore>(sp => sp.GetRequiredService<RedisPlayerStore>());
builder.Services.AddSingleton<IPlayerStatsStore>(sp => sp.GetRequiredService<RedisPlayerStore>());
builder.Services.AddSingleton<ILeaderboardStore>(sp => sp.GetRequiredService<RedisPlayerStore>());
builder.Services.AddSingleton<PlayerOperations>();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("PlayerService"))
    .WithMetrics(metrics => metrics
        .AddMeter(ServiceMetrics.MeterName, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel")
        .AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5]
        })
        .AddView("player.redis.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [0.0005, 0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2]
        })
        .AddPrometheusExporter());
builder.Services.AddRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
{
    Timeout = TimeSpan.FromSeconds(5), TimeoutStatusCode = 503,
    WriteTimeoutResponse = async context => await context.Response.WriteAsJsonAsync(new { error = "request_timeout", retry = "Reuse the same requestId for mutations." })
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);

var app = builder.Build();
try
{
    // Resolve the singleton connection before accepting requests, avoiding a connection
    // initialization lock shared by an entire first-request burst.
    await app.Services.GetRequiredService<RedisPlayerStore>().InitializeAsync();
}
catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
{
    app.Logger.LogWarning("Redis unavailable at startup; readiness will fail until it recovers.");
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try { await next(context); }
    catch (Exception exception) when (exception is RedisException or RedisTimeoutException)
    {
        app.Logger.LogWarning("Redis request failed: {FailureType}", exception.GetType().Name);
        context.Response.StatusCode = 503;
        context.Response.Headers.RetryAfter = "1";
        await context.Response.WriteAsJsonAsync(new { error = "storage_unavailable", retry = "Reuse the same requestId for mutations; login is not safely retryable." });
    }
});
app.UseRequestTimeouts();
app.MapPlayerEndpoints();
app.MapPrometheusScrapingEndpoint();
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (IConnectionMultiplexer connection, HttpContext context) =>
{
    await connection.GetDatabase().PingAsync(CommandFlags.DemandMaster).WaitAsync(context.RequestAborted);
    return Results.Ok(new { status = "ready" });
});
app.Run();

public partial class Program;
