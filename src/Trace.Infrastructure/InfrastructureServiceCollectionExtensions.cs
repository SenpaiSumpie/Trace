using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Trace.Infrastructure.Persistence;

namespace Trace.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public const string ConnectionStringName = "Trace";
    public const string ApplyMigrationsOnStartupKey = "Database:ApplyMigrationsOnStartup";
    public const string SeedDemoDataKey = "Database:SeedDemoData";

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
        var applyMigrations = IsEnabled(configuration, ApplyMigrationsOnStartupKey);
        var seedDemoData = IsEnabled(configuration, SeedDemoDataKey);

        if (seedDemoData && services.GetRequiredService<IHostEnvironment>().IsProduction())
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
            await DemoData.SeedAsync(db, cancellationToken);
        }
    }

    private static bool IsEnabled(IConfiguration configuration, string key) =>
        configuration[key] is { } value
        && (bool.TryParse(value, out var enabled)
            ? enabled
            : throw new InvalidOperationException($"'{key}' must be 'true' or 'false' but was '{value}'."));
}
