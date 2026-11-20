using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Trace.Application.Indexing;
using Trace.Application.Retrieval;
using Trace.Infrastructure.Embeddings;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "Trace";
    public const string ApplyMigrationsOnStartupKey = "Database:ApplyMigrationsOnStartup";
    public const string SeedDemoDataKey = "Database:SeedDemoData";

    /// <summary>Directory of the demo fixture repository; relative paths resolve against the content root.</summary>
    public const string DemoFixturePathKey = "Demo:FixturePath";

    public const string EmbeddingProviderKey = "Embeddings:Provider";

    public static IServiceCollection AddTraceInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<TraceDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>()
                .GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' is not configured.");
            TraceDbContext.Configure(options, connectionString);
        });

        services.AddSingleton(CreateEmbeddingProvider);
        services.AddScoped<ISnapshotStore, EfSnapshotStore>();
        services.AddScoped<SnapshotIndexer>();
        services.AddScoped<ISnapshotReader, EfSnapshotReader>();
        services.AddScoped<SourceTools>();
        services.AddScoped<CitationValidator>();

        return services;
    }

    /// <summary>
    /// Applies migrations and/or seeds demo data when the corresponding settings are enabled.
    /// Demo data is refused outright in the Production environment.
    /// </summary>
    public static async Task InitializeTraceDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetRequiredService<IHostEnvironment>();
        var applyMigrations = IsEnabled(configuration, ApplyMigrationsOnStartupKey);
        var seedDemoData = IsEnabled(configuration, SeedDemoDataKey);

        if (seedDemoData && environment.IsProduction())
        {
            throw new InvalidOperationException(
                $"'{SeedDemoDataKey}' must not be enabled in the Production environment.");
        }

        if (!applyMigrations && !seedDemoData)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceDbContext>();

        if (applyMigrations)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        if (seedDemoData)
        {
            var fixtureDirectory = configuration[DemoFixturePathKey] is { Length: > 0 } path
                ? Path.GetFullPath(path, environment.ContentRootPath)
                : null;
            await DemoData.SeedAsync(
                db, scope.ServiceProvider.GetRequiredService<SnapshotIndexer>(), fixtureDirectory, cancellationToken);
        }
    }

    private static IEmbeddingProvider CreateEmbeddingProvider(IServiceProvider services)
    {
        var provider = services.GetRequiredService<IConfiguration>()[EmbeddingProviderKey];
        switch (provider)
        {
            case DeterministicFakeEmbeddingProvider.ProviderName:
                if (services.GetRequiredService<IHostEnvironment>().IsProduction())
                {
                    throw new InvalidOperationException(
                        $"'{EmbeddingProviderKey}' must not be '{provider}' in the Production environment.");
                }

                return new DeterministicFakeEmbeddingProvider();

            case null or "":
                throw new InvalidOperationException($"'{EmbeddingProviderKey}' is not configured.");

            default:
                throw new InvalidOperationException($"'{EmbeddingProviderKey}' has unknown value '{provider}'.");
        }
    }

    private static bool IsEnabled(IConfiguration configuration, string key) =>
        configuration[key] is { } value
        && (bool.TryParse(value, out var enabled)
            ? enabled
            : throw new InvalidOperationException($"'{key}' must be 'true' or 'false' but was '{value}'."));
}
