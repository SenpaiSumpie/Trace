using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Trace.Infrastructure.Persistence;

/// <summary>
/// Used only by the <c>dotnet ef</c> tooling. Generating migrations needs no database; commands
/// that connect (e.g. <c>database update</c>) read <c>ConnectionStrings__Trace</c> from the environment.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TraceDbContext>
{
    public TraceDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Trace")
            ?? "Host=localhost;Database=trace";

        var builder = new DbContextOptionsBuilder<TraceDbContext>();
        TraceDbContext.Configure(builder, connectionString);
        return new TraceDbContext(builder.Options, TimeProvider.System);
    }
}
