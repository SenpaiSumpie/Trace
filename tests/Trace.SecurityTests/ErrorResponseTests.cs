using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Trace.SecurityTests;

/// <summary>
/// Error responses must be structured ProblemDetails and must not disclose internals,
/// including when the host runs in the Development environment.
/// </summary>
public class ErrorResponseTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Unknown_route_returns_problem_details_without_internals(string environment)
    {
        using var client = factory.WithEnvironment(environment).CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(body);
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_disclose_only_the_aggregate_status(string path)
    {
        const string connectionString = "Host=127.0.0.1;Port=1;Database=trace;Username=trace;Password=s3cret-value;Timeout=1";
        using var client = factory
            .WithEnvironment("Development", ("ConnectionStrings:Trace", connectionString))
            .CreateClient();

        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(body, new[] { "Healthy", "Unhealthy" });
    }
}
