using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Trace.Infrastructure;

namespace Trace.IntegrationTests.Support;

internal static class ApiFactoryExtensions
{
    /// <summary>Points the API at a specific database and controls startup initialization.</summary>
    public static WebApplicationFactory<Program> WithDatabase(
        this WebApplicationFactory<Program> factory,
        string connectionString,
        bool initializeOnStartup) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{InfrastructureServiceCollectionExtensions.ConnectionStringName}"] = connectionString,
                [InfrastructureServiceCollectionExtensions.ApplyMigrationsOnStartupKey] = initializeOnStartup.ToString(),
                [InfrastructureServiceCollectionExtensions.SeedDemoDataKey] = initializeOnStartup.ToString(),
            })));
}
