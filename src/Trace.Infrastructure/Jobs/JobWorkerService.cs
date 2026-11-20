using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trace.Application.Indexing;
using Trace.Application.Jobs;
using Trace.Infrastructure.Indexing;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure.Jobs;

/// <summary>
/// Runs jobs from the queue until the host stops. On shutdown the job in progress is handed
/// back to the queue; if the process is killed instead, its lease expires and another worker
/// takes it over.
/// </summary>
internal sealed partial class JobWorkerService(
    IServiceScopeFactory scopes,
    IJobSignal signal,
    JobOptions options,
    IConfiguration configuration,
    ILogger<JobWorkerService> logger) : BackgroundService
{
    public const string ConcurrencyKey = "Jobs:Concurrency";
    private const int DefaultConcurrency = 2;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = int.TryParse(configuration[ConcurrencyKey], out var configured) && configured > 0
            ? configured
            : DefaultConcurrency;

        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(_ => RunLoopAsync(stoppingToken)));
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting (and, in the API, the database be migrated) first.
        await Task.Yield();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool worked;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    worked = await scope.ServiceProvider.GetRequiredService<JobProcessor>()
                        .ProcessNextAsync(stoppingToken);
                }

                if (!worked)
                {
                    await signal.WaitAsync(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Typically the database being briefly unavailable. Keep the worker alive.
                LogLoopFailure(logger, exception);
                try
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The job worker could not poll the queue; retrying.")]
    private static partial void LogLoopFailure(ILogger logger, Exception exception);
}

/// <summary>
/// The only repository source so far: the demo repository's fixture directory, when configured.
/// GitHub-backed repositories get their own source with the GitHub integration.
/// </summary>
internal sealed class ConfiguredRepositorySourceResolver(
    TraceDbContext db,
    IConfiguration configuration,
    IHostEnvironment environment) : IRepositorySourceResolver
{
    public async Task<IRepositorySource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default)
    {
        if (configuration[InfrastructureServiceCollectionExtensions.DemoFixturePathKey] is not { Length: > 0 } path)
        {
            return null;
        }

        var isDemo = await db.Repositories.AnyAsync(
            r => r.Id == repositoryId && r.Owner == DemoData.RepositoryOwner && r.Name == DemoData.RepositoryName,
            cancellationToken);

        return isDemo
            ? new LocalDirectorySource(Path.GetFullPath(path, environment.ContentRootPath), DemoData.Branch)
            : null;
    }
}
