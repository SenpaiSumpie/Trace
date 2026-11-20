using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
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
        using var client = factory
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment))
            .CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var problem = JsonDocument.Parse(body);
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_endpoint_discloses_only_the_aggregate_status()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
