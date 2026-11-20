using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Indexing;
using Trace.Application.Investigations;

namespace Trace.SecurityTests;

/// <summary>Demo data is for explicit non-production use only.</summary>
public class DemoDataGuardTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Api_refuses_to_start_in_production_with_demo_seeding_enabled()
    {
        var production = factory.WithEnvironment("Production", ("Database:SeedDemoData", "true"));

        var exception = Assert.Throws<InvalidOperationException>(() => production.CreateClient());

        Assert.Contains("Database:SeedDemoData", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_refuses_to_start_with_an_unparseable_startup_flag()
    {
        var misconfigured = factory.WithEnvironment("Production", ("Database:SeedDemoData", "yes"));

        var exception = Assert.Throws<InvalidOperationException>(() => misconfigured.CreateClient());

        Assert.Contains("Database:SeedDemoData", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", "deterministic-fake")]
    [InlineData("Production", null)]
    [InlineData("Development", "no-such-provider")]
    public void Embedding_provider_is_refused_when_fake_in_production_missing_or_unknown(string environment, string? provider)
    {
        var api = factory.WithEnvironment(environment, ("Embeddings:Provider", provider ?? string.Empty));

        var exception = Assert.Throws<InvalidOperationException>(
            () => api.Services.GetRequiredService<IEmbeddingProvider>());

        Assert.Contains("Embeddings:Provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fake_embedding_provider_is_available_outside_production()
    {
        var api = factory.WithEnvironment("Development", ("Embeddings:Provider", "deterministic-fake"));

        Assert.Equal("fake-hash-v1", api.Services.GetRequiredService<IEmbeddingProvider>().ModelId);
    }

    [Theory]
    [InlineData("Production", "deterministic-fake")]
    [InlineData("Production", "")]
    [InlineData("Development", "no-such-provider")]
    public void Chat_model_is_refused_when_fake_in_production_missing_or_unknown(string environment, string provider)
    {
        var api = factory.WithEnvironment(environment, ("Chat:Provider", provider));

        var exception = Assert.Throws<InvalidOperationException>(() => api.Services.GetRequiredService<IChatModel>());

        Assert.Contains("Chat:Provider", exception.Message, StringComparison.Ordinal);
    }
}
