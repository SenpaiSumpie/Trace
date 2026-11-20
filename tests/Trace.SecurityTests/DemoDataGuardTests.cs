using Microsoft.AspNetCore.Mvc.Testing;

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
}
