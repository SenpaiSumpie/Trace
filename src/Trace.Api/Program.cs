using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Trace.Api;
using Trace.Infrastructure;
using Trace.Infrastructure.Persistence;
using Trace.Infrastructure.Telemetry;

const string ReadyTag = "ready";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddProblemDetails();

// A malformed request body is the client's error (400) in every environment; by default
// Development throws instead, which would surface as a 500.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddTraceInfrastructure();
builder.Services.AddTraceTelemetry(
    builder.Configuration,
    "trace-api",
    // Health probes arrive every few seconds and would drown out real requests.
    tracing => tracing.AddAspNetCoreInstrumentation(options =>
        options.Filter = context => !context.Request.Path.StartsWithSegments("/health")),
    metrics => metrics.AddAspNetCoreInstrumentation());
builder.AddTraceAuthentication();

// Starting investigations and index jobs costs model calls and worker time, so those requests
// are limited per client address. There are no users yet to limit by.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy(V1Endpoints.WriteRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            // Read when first needed so every configuration source has been applied.
            PermitLimit = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimits:WritesPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
});
if (InfrastructureServiceCollectionExtensions.ShouldProcessJobsInThisHost(builder.Configuration))
{
    builder.Services.AddTraceJobProcessing();
}

builder.Services.AddHealthChecks()
    .AddDbContextCheck<TraceDbContext>("database", tags: [ReadyTag]);

// Behind a reverse proxy that terminates TLS, the original scheme and client address arrive
// in X-Forwarded-* headers. They are trusted only when this is switched on, which must only
// be done when the API is not reachable except through that proxy.
if (builder.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

// "--migrate": apply pending database migrations and exit. Run once per release, before the
// new version starts serving, instead of migrating from every instance at startup.
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<TraceDbContext>().Database.MigrateAsync();
    return;
}

if (app.Configuration.GetValue("Proxy:TrustForwardedHeaders", false))
{
    app.UseForwardedHeaders();
}

app.UseCorrelationId();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseRateLimiter();

// Liveness: the process is up. Readiness: its dependencies are reachable.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

app.MapV1Endpoints();
app.MapAuthEndpoints();

await app.Services.InitializeTraceDatabaseAsync();

await app.RunAsync();
