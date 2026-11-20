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
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TraceDbContext>("database", tags: [ReadyTag]);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Liveness: the process is up. Readiness: its dependencies are reachable.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) });

app.MapV1Endpoints();

await app.Services.InitializeTraceDatabaseAsync();

await app.RunAsync();
