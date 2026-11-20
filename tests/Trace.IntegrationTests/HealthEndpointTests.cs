using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class HealthEndpointTests(PostgresFixture postgres, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=trace;Username=trace;Password=unused;Timeout=1";

    [Fact]
    public async Task Liveness_reports_healthy_without_a_database()
    {
        using var client = factory.WithDatabase(UnreachableDatabase, initializeOnStartup: false).CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_healthy_when_the_database_is_reachable()
    {
        await using var database = await postgres.CreateDatabaseAsync();
        using var client = factory.WithDatabase(database.ConnectionString, initializeOnStartup: false).CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_when_the_database_is_unreachable()
    {
        using var client = factory.WithDatabase(UnreachableDatabase, initializeOnStartup: false).CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }
}
