using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Trace.Api;
using Trace.Infrastructure;
using Trace.Infrastructure.Persistence;

const string ReadyTag = "ready";

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddProblemDetails();

// A malformed request body is the client's error (400) in every environment; by default
// Development throws instead, which would surface as a 500.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddTraceInfrastructure();
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

var app = builder.Build();

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
