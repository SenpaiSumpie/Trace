using Npgsql;

namespace Trace.Evals;

/// <summary>
/// A new, empty database for one evaluation run, created next to the configured one on the same
/// PostgreSQL server and dropped when the run ends.
/// <para>
/// The configured database (for example the development database) is used only as the place to
/// connect to in order to issue <c>CREATE DATABASE</c> and <c>DROP DATABASE</c>: nothing in it
/// is read or written. Only a database this instance created is ever dropped.
/// </para>
/// </summary>
public sealed class ThrowawayDatabase : IAsyncDisposable
{
    public const string NamePrefix = "trace_eval_";

    private readonly string serverConnectionString;
    private readonly bool keep;
    private bool dropped;

    private ThrowawayDatabase(string serverConnectionString, string name, bool keep)
    {
        this.serverConnectionString = serverConnectionString;
        this.keep = keep;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Database = name }.ConnectionString;
    }

    /// <summary>Generated here from fixed characters only, so it is safe to place in SQL.</summary>
    public string Name { get; }

    public string ConnectionString { get; }

    /// <param name="serverConnectionString">Any database on the server the user may connect to and create databases from.</param>
    /// <param name="keep">Leave the database in place on dispose, for looking at a run afterwards.</param>
    public static async Task<ThrowawayDatabase> CreateAsync(
        string serverConnectionString,
        bool keep = false,
        CancellationToken cancellationToken = default)
    {
        var database = new ThrowawayDatabase(serverConnectionString, $"{NamePrefix}{Guid.NewGuid():N}", keep);
        await database.ExecuteOnServerAsync($"CREATE DATABASE {database.Name}", cancellationToken);
        return database;
    }

    public async ValueTask DisposeAsync()
    {
        if (keep || dropped)
        {
            return;
        }

        // FORCE closes connections still held open by a connection pool.
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)", CancellationToken.None);
        dropped = true;
    }

    private async Task ExecuteOnServerAsync(string sql, CancellationToken cancellationToken)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(serverConnectionString) { Pooling = false }.ConnectionString;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
