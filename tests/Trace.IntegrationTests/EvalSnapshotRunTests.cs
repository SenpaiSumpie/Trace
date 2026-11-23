using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Domain;
using Trace.Evals;
using Trace.Infrastructure.Indexing;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// <c>run --snapshot</c>: evaluating a snapshot that was indexed earlier through the application
/// (ADR-014). The fixture stands in for a private repository reached through GitHub. What must
/// hold: the database it lives in is never changed, only that one snapshot leaves it, nothing
/// that names the repository reaches the output, and the copy gives nobody a way to see the
/// repository through the application.
/// </summary>
[Collection(PostgresCollection.Name)]
public partial class EvalSnapshotRunTests(PostgresFixture postgres)
{
    private const string PrivateOwner = "octo-private-owner";
    private const string PrivateName = "octo-private-repository";
    private const string OtherRepositoryPath = "notes/other-repository.md";

    private static readonly string Questions = SampleRepo.Path + ".questions.json";

    [Fact]
    public async Task A_snapshot_indexed_through_the_application_is_evaluated_from_a_copy_and_its_database_is_left_untouched()
    {
        await using var source = await SourceDatabaseAsync();
        using var outputDirectory = new TempDirectory();
        var output = new StringWriter();
        var error = new StringWriter();
        var before = await FingerprintAsync(source.Database.ConnectionString);
        var runDatabasesBefore = await RunDatabaseCountAsync(source.Database.ConnectionString);

        var exitCode = await EvalCommand.RunAsync(
            ["run", "--dataset", Questions, "--snapshot", source.Index.SnapshotId.ToString(), "--output", outputDirectory.Path],
            output,
            error,
            PointAt(source));

        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains($"({source.Index.ChunkCount} passages, copied from the configured database)", output.ToString(), StringComparison.Ordinal);

        var file = Assert.Single(Directory.GetFiles(outputDirectory.Path));
        var result = EvalRunResult.Load(file);

        // The snapshot evaluated is the indexed one: same revision, same index version, nothing re-embedded.
        Assert.Equal(EvalRunStatus.Completed, result.Status);
        Assert.Equal(source.Index.CommitSha, result.Snapshot.CommitSha);
        Assert.Equal((LineChunker.Version, source.EmbeddingModel), (result.Snapshot.ChunkerVersion, result.Snapshot.EmbeddingModel));
        Assert.Equal(("sample-repo", "all", 34, true), (result.Dataset.Name, result.Dataset.Split, result.Dataset.QuestionCount, result.Dataset.RevisionMatchesSnapshot));
        Assert.All(result.Questions, question =>
        {
            Assert.Contains(question.Outcome, new[] { EvalOutcomes.Answered, EvalOutcomes.InsufficientEvidence });
            Assert.NotNull(question.InvestigationId);
            Assert.Null(question.Retrieval!.ErrorCode);
        });

        // The same numbers a run that indexes the fixture directory gives (EvalRunnerTests): the
        // copy carries the passages, their embeddings and the full-text index unchanged.
        var metrics = EvalMetrics.Report(result).Metrics;
        Assert.Equal((31, 27, 0), (metrics[0].RecallAt10.Labeled, metrics[0].RecallAt10.QuestionsMeasured, metrics[0].RecallAt10.QuestionsNotMeasured));
        Assert.InRange(metrics[0].RecallAt10.Found, 30, 31);
        Assert.Equal((15, 15), (metrics[1].RecallAt10.Found, metrics[1].RecallAt10.Labeled));
        Assert.Equal(1.0, metrics[0].CitationValidity.Value);

        // Nothing names the repository the snapshot came from: not the output, not the result file.
        foreach (var text in new[] { output.ToString(), await File.ReadAllTextAsync(file) })
        {
            Assert.DoesNotContain(PrivateOwner, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PrivateName, text, StringComparison.Ordinal);
        }

        // The database the snapshot lives in has the same rows with the same timestamps, and the run's own database is gone.
        Assert.Equal(before, await FingerprintAsync(source.Database.ConnectionString));
        Assert.Equal(runDatabasesBefore, await RunDatabaseCountAsync(source.Database.ConnectionString));

        // The repository is as invisible through the application as before: the copy lived in
        // another database, and the check an API caller meets was neither changed nor skipped.
        await using var scope = source.Database.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IRepositoryCatalog>().IsVisibleAsync(source.RepositoryId));
        var refused = await Assert.ThrowsAsync<InvestigationRequestException>(
            () => scope.ServiceProvider.GetRequiredService<QuestionInvestigator>().CreateAsync(
                new AskQuestion(source.RepositoryId, source.Index.SnapshotId, "Why does the first page skip results?")));
        Assert.Equal(InvestigationRequestError.RepositoryNotFound, refused.Error);
    }

    [Fact]
    public async Task Only_the_chosen_snapshot_is_copied_so_a_model_cannot_reach_another_repository_of_the_same_database()
    {
        await using var source = await SourceDatabaseAsync();

        // A second repository in the same database, with a file the evaluated one does not have.
        using var otherDirectory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(otherDirectory.Path, "notes"));
        await File.WriteAllTextAsync(
            Path.Combine(otherDirectory.Path, OtherRepositoryPath), "# Other repository\n\nThe launch code word is zebracorn.\n");
        Guid otherRepositoryId;
        await using (var db = source.Database.CreateContext())
        {
            var other = Repository.Create("octo-other-owner", "octo-other-repository", "main", RepositoryVisibility.Private);
            db.Repositories.Add(other);
            await db.SaveChangesAsync();
            otherRepositoryId = other.Id;
        }

        var otherIndex = await source.Database.IndexAsync(otherRepositoryId, new LocalDirectorySource(otherDirectory.Path, "main"));

        using var outputDirectory = new TempDirectory();
        var dataset = Path.Combine(outputDirectory.Path, "hostile.questions.json");
        await File.WriteAllTextAsync(
            dataset,
            """
            {
              "name": "hostile",
              "datasetVersion": "1",
              "questions": [{ "id": "code-word", "split": "dev", "question": "What is the launch code word?", "answerable": false }]
            }
            """);

        ScriptedChatModel script = null!;
        script = new ScriptedChatModel(
            _ => ModelTurn.Call(
                script.Read(OtherRepositoryPath, 1, 3),
                script.Tool("search_code", new { query = "zebracorn", repositoryId = otherRepositoryId, snapshotId = otherIndex.SnapshotId }),
                script.Search("zebracorn")),
            _ => ScriptedChatModel.Answer("The code word is zebracorn.", (OtherRepositoryPath, 1, 3)));
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await EvalCommand.RunAsync(
            ["run", "--dataset", dataset, "--snapshot", source.Index.SnapshotId.ToString(), "--output", outputDirectory.Path, "--keep-database"],
            output,
            error,
            builder =>
            {
                PointAt(source)(builder);
                builder.Services.AddSingleton<IChatModel>(script);
            });

        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        var kept = new NpgsqlConnectionStringBuilder(source.Database.ConnectionString)
        {
            Database = RunDatabaseName().Match(output.ToString()).Value,
        };
        try
        {
            var file = Assert.Single(Directory.GetFiles(outputDirectory.Path), path => !path.EndsWith(".questions.json", StringComparison.Ordinal));
            var question = Assert.Single(EvalRunResult.Load(file).Questions);

            // The file of the other repository cannot be read, a tool call cannot name another
            // repository, the honest search finds nothing of it, and the claimed citation is discarded.
            Assert.Equal(
                [("read_file_range", EvalToolCall.Failed), ("search_code", EvalToolCall.Failed), ("search_code", EvalToolCall.Called)],
                question.ToolCalls.Select(call => (call.Tool, call.Status)));
            Assert.Equal(["path_not_found", "invalid_argument", null], question.ToolCalls.Select(call => call.ErrorCode));
            Assert.Equal((EvalOutcomes.InsufficientEvidence, 1), (question.Outcome, question.RejectedCitations));
            Assert.Empty(question.Evidence);
            Assert.DoesNotContain("The code word is zebracorn", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
            Assert.DoesNotContain("launch code word is", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

            // The run's database holds one repository, named after the dataset, and one snapshot:
            // the chosen one, passage for passage. No account, installation or access row came along.
            Assert.Equal("trace-eval/hostile/public/", await ScalarAsync(kept.ConnectionString, "SELECT string_agg(owner || '/' || name || '/' || visibility || '/' || coalesce(installation_id::text, ''), ',') FROM repositories"));
            Assert.Equal(source.Index.SnapshotId, await ScalarAsync(kept.ConnectionString, "SELECT id FROM index_snapshots"));
            Assert.Equal((long)source.Index.ChunkCount, await ScalarAsync(kept.ConnectionString, "SELECT count(*) FROM source_chunks"));
            Assert.Equal(0L, await ScalarAsync(kept.ConnectionString, $"SELECT count(*) FROM source_chunks WHERE path = '{OtherRepositoryPath}' OR text LIKE '%zebracorn%'"));
            Assert.Equal(
                0L,
                await ScalarAsync(kept.ConnectionString, "SELECT (SELECT count(*) FROM users) + (SELECT count(*) FROM github_installations) + (SELECT count(*) FROM repository_access)"));

            // Passage text, line ranges and embeddings are the stored ones, not recomputed.
            const string passages = "SELECT md5(string_agg(id::text || path || start_line || end_line || content_hash || embedding::text, '' ORDER BY id)) FROM source_chunks WHERE snapshot_id = ";
            Assert.Equal(
                await ScalarAsync(source.Database.ConnectionString, passages + $"'{source.Index.SnapshotId}'"),
                await ScalarAsync(kept.ConnectionString, passages + $"'{source.Index.SnapshotId}'"));
        }
        finally
        {
            await ScalarAsync(source.Database.ConnectionString, $"DROP DATABASE {kept.Database} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task A_snapshot_that_is_unknown_unfinished_or_from_another_embedding_model_stops_the_run_before_any_question()
    {
        await using var source = await SourceDatabaseAsync();
        Guid buildingSnapshotId;
        await using (var db = source.Database.CreateContext())
        {
            var building = IndexSnapshot.Start(source.RepositoryId, new string('b', 40), "main", LineChunker.Version, source.EmbeddingModel);
            db.IndexSnapshots.Add(building);
            await db.SaveChangesAsync();
            buildingSnapshotId = building.Id;
        }

        using var outputDirectory = new TempDirectory();
        var model = new ScriptedChatModel(_ => throw new InvalidOperationException("No question may be asked."));
        var before = await FingerprintAsync(source.Database.ConnectionString);
        var runDatabasesBefore = await RunDatabaseCountAsync(source.Database.ConnectionString);

        async Task<string> RunAsync(Guid snapshotId, Action<HostApplicationBuilder>? configure = null)
        {
            var error = new StringWriter();
            var exitCode = await EvalCommand.RunAsync(
                ["run", "--dataset", Questions, "--snapshot", snapshotId.ToString(), "--output", outputDirectory.Path],
                new StringWriter(),
                error,
                builder =>
                {
                    PointAt(source)(builder);
                    builder.Services.AddSingleton<IChatModel>(model);
                    configure?.Invoke(builder);
                });
            Assert.Equal(EvalCommand.ExitError, exitCode);
            return error.ToString();
        }

        var unknown = Guid.CreateVersion7();
        Assert.Contains($"has no completed snapshot with the ID {unknown}", await RunAsync(unknown), StringComparison.Ordinal);
        Assert.Contains($"has no completed snapshot with the ID {buildingSnapshotId}", await RunAsync(buildingSnapshotId), StringComparison.Ordinal);

        var mismatch = await RunAsync(
            source.Index.SnapshotId,
            builder => builder.Services.AddSingleton<IEmbeddingProvider>(new OtherEmbeddingModel()));
        Assert.Contains(
            $"indexed with the embedding model '{source.EmbeddingModel}' but this run is configured for '{OtherEmbeddingModel.Id}'",
            mismatch,
            StringComparison.Ordinal);

        // No question was asked, no file written, no database left behind, and the source is unchanged.
        Assert.Empty(model.Requests);
        Assert.Empty(Directory.GetFiles(outputDirectory.Path));
        Assert.Equal(runDatabasesBefore, await RunDatabaseCountAsync(source.Database.ConnectionString));
        Assert.Equal(before, await FingerprintAsync(source.Database.ConnectionString));
    }

    [Fact]
    public async Task The_snapshot_option_is_validated_before_anything_is_created()
    {
        static void MustNotConfigure(HostApplicationBuilder builder) =>
            Assert.Fail("The run must not get as far as building its services.");

        async Task<string> RunAsync(params string[] arguments)
        {
            var error = new StringWriter();
            Assert.Equal(
                EvalCommand.ExitError,
                await EvalCommand.RunAsync(["run", "--dataset", Questions, .. arguments], new StringWriter(), error, MustNotConfigure));
            Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);
            return error.ToString();
        }

        Assert.Contains("'--source' or '--snapshot' is required", await RunAsync(), StringComparison.Ordinal);
        Assert.Contains("'--snapshot' must be a snapshot ID", await RunAsync("--snapshot", "latest"), StringComparison.Ordinal);
        Assert.Contains("'--snapshot' must be a snapshot ID", await RunAsync("--snapshot", Guid.Empty.ToString()), StringComparison.Ordinal);
        Assert.Contains("'--snapshot' needs a value", await RunAsync("--snapshot"), StringComparison.Ordinal);
        Assert.Contains(
            "'--source' and '--snapshot' cannot be combined",
            await RunAsync("--source", SampleRepo.Path, "--snapshot", Guid.CreateVersion7().ToString()),
            StringComparison.Ordinal);

        // A snapshot run writes under eval/local/ unless told otherwise: results of a private repository must not land elsewhere by default.
        var options = EvalCommand.ParseArguments(["run", "--dataset", Questions, "--snapshot", "01234567-89ab-7cde-8f01-23456789abcd"]);
        Assert.Equal((Guid.Parse("01234567-89ab-7cde-8f01-23456789abcd"), null, null), (options.SnapshotId, options.SourceDirectory, options.OutputDirectory));
        Assert.Equal("eval/local/results", EvalCommand.DefaultSnapshotOutputDirectory);
    }

    [Fact]
    public async Task The_connection_the_snapshot_is_read_through_cannot_change_the_database()
    {
        await using var source = await postgres.CreateDatabaseAsync();
        var readOnly = IndexedSnapshotCopy.ReadOnly(source.ConnectionString);

        Assert.Equal(0L, await ScalarAsync(readOnly, "SELECT count(*) FROM repositories"));
        foreach (var statement in new[]
        {
            "DELETE FROM repositories",
            "INSERT INTO users (id, github_user_id, login, created_at, updated_at) VALUES (gen_random_uuid(), 1, 'x', now(), now())",
            "CREATE TABLE eval_scratch (id integer)",
        })
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(readOnly, statement));
            Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, exception.SqlState);
        }
    }

    private static Action<HostApplicationBuilder> PointAt(SourceDatabase source) =>
        builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Trace"] = source.Database.ConnectionString });

    /// <summary>
    /// Stands in for the developer's database: the fixture indexed as a private repository that is
    /// reached through a GitHub installation, which no anonymous caller may see.
    /// </summary>
    private async Task<SourceDatabase> SourceDatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid repositoryId;
        await using (var db = database.CreateContext())
        {
            var installation = GitHubInstallation.Create(424_242, PrivateOwner);
            db.GitHubInstallations.Add(installation);
            var repository = Repository.FromGitHub(installation.Id, 515_151, PrivateOwner, PrivateName, "main", RepositoryVisibility.Private);
            db.Repositories.Add(repository);
            await db.SaveChangesAsync();
            repositoryId = repository.Id;
        }

        var index = await database.IndexAsync(repositoryId, SampleRepo.Source());
        await using var scope = database.CreateScope();
        return new SourceDatabase(database, repositoryId, index, scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>().ModelId);
    }

    /// <summary>Row counts and latest change of every table a run could touch.</summary>
    private static async Task<string> FingerprintAsync(string connectionString)
    {
        var tables = new[]
        {
            "repositories", "index_snapshots", "source_chunks", "investigations", "investigation_events",
            "evidence_refs", "jobs", "users", "github_installations", "repository_access",
        };
        var parts = new List<string>();
        foreach (var table in tables)
        {
            parts.Add($"{table}:{await ScalarAsync(connectionString, $"SELECT count(*) || '@' || coalesce(max(updated_at)::text, '-') FROM {table}")}");
        }

        return string.Join(' ', parts);
    }

    private static async Task<long> RunDatabaseCountAsync(string connectionString) =>
        (long)(await ScalarAsync(connectionString, $"SELECT count(*) FROM pg_database WHERE datname LIKE '{ThrowawayDatabase.NamePrefix}%'"))!;

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [GeneratedRegex("trace_eval_[0-9a-f]{32}")]
    private static partial Regex RunDatabaseName();

    private sealed record SourceDatabase(TestDatabase Database, Guid RepositoryId, IndexResult Index, string EmbeddingModel) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    /// <summary>An embedding provider other than the one the source snapshot was indexed with. Never asked to embed.</summary>
    private sealed class OtherEmbeddingModel : IEmbeddingProvider
    {
        public const string Id = "another-embedding-model";

        public string ModelId => Id;

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Nothing may be embedded for a snapshot of another model.");
    }
}
