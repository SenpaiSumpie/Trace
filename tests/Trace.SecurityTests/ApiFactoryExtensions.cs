using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Trace.SecurityTests;

internal static class ApiFactoryExtensions
{
    /// <summary>
    /// Runs the API in the given environment with explicit settings. Startup database
    /// initialization is off unless a test turns it on, so no test here needs a database.
    /// </summary>
    public static WebApplicationFactory<Program> WithEnvironment(
        this WebApplicationFactory<Program> factory,
        string environment,
        params (string Key, string Value)[] settings) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>
                {
                    ["Database:ApplyMigrationsOnStartup"] = "false",
                    ["Database:SeedDemoData"] = "false",
                };
                foreach (var (key, value) in settings)
                {
                    values[key] = value;
                }

                configuration.AddInMemoryCollection(values);
            });
        });
}
