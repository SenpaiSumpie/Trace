using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Domain;
using Trace.Evals;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// The evaluation runner against a real database. The runner has no investigation logic of its
/// own, so these tests check two things: that what it records is what the production pipeline
/// stored, and that nothing a question or a model does can stop a run.
/// </summary>
[Collection(PostgresCollection.Name)]
public partial class EvalRunnerTests(PostgresFixture postgres)
{
    private const string TaskRepositoryPath = "src/TaskBoard.Api/Tasks/TaskRepository.cs";
    private const string TraceCommit = "1234567890abcdef1234567890abcdef12345678";

    private static readonly ModelPrices Prices = new(
        InputPerMillion: 10m, OutputPerMillion: 50m, CacheReadPerMillion: 1m, CacheWritePerMillion: 12.5m);

    private static readonly ModelUsage TurnUsage = new(InputTokens: 1_000, OutputTokens: 200, CacheReadTokens: 300, CacheWriteTokens: 100);

    [Fact]
    public async Task One_command_runs_the_fixture_questions_with_the_fake_providers_and_writes_a_result_file()
    {
        // Stands in for the developer's database: the run may connect to it but must not change it.
        await using var configured = await postgres.CreateDatabaseAsync(migrate: false);
        using var outputDirectory = new TempDirectory();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await EvalCommand.RunAsync(
            ["run", "--dataset", SampleRepo.Path + ".questions.json", "--source", SampleRepo.Path, "--output", outputDirectory.Path],
            output,
            error,
            builder => builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Trace"] = configured.ConnectionString }));

        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        Assert.Equal(string.Empty, error.ToString());

        var file = Assert.Single(Directory.GetFiles(outputDirectory.Path));
        Assert.Matches(@"^sample-repo-\d{8}T\d{6}Z-[0-9a-f]{8}\.json$", Path.GetFileName(file));
        Assert.Contains($"Result file: {file}", output.ToString(), StringComparison.Ordinal);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        var run = document.RootElement;

        // Per run: the TRACE commit, dataset name and version, snapshot commit, chunker and
        // embedding versions, chat model and prompt version, start and end time.
        Assert.Equal(1, run.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Matches("^[0-9a-f]{40}$", run.GetProperty("traceCommit").GetString());
        Assert.Contains(run.GetProperty("traceCommitDirty").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal("sample-repo", run.GetProperty("dataset").GetProperty("name").GetString());
        Assert.Equal("1", run.GetProperty("dataset").GetProperty("version").GetString());
        Assert.Equal("all", run.GetProperty("dataset").GetProperty("split").GetString());
        Assert.Equal(11, run.GetProperty("dataset").GetProperty("questionCount").GetInt32());
        Assert.True(run.GetProperty("dataset").GetProperty("revisionMatchesSnapshot").GetBoolean());
        Assert.Equal(
            run.GetProperty("dataset").GetProperty("revision").GetString(),
            run.GetProperty("snapshot").GetProperty("commitSha").GetString());
        Assert.Equal(LineChunker.Version, run.GetProperty("snapshot").GetProperty("chunkerVersion").GetString());
        Assert.Equal("deterministic-fake", run.GetProperty("snapshot").GetProperty("embeddingProvider").GetString());
        Assert.False(string.IsNullOrEmpty(run.GetProperty("snapshot").GetProperty("embeddingModel").GetString()));
        Assert.Equal("deterministic-fake", run.GetProperty("chat").GetProperty("provider").GetString());
        Assert.Equal("fake-extractive-v1", run.GetProperty("chat").GetProperty("model").GetString());
        Assert.Equal(QuestionPrompt.Version, run.GetProperty("chat").GetProperty("promptVersion").GetString());
        Assert.Equal("USD", run.GetProperty("prices").GetProperty("currency").GetString());
        Assert.True(run.GetProperty("startedAt").GetDateTimeOffset() <= run.GetProperty("endedAt").GetDateTimeOffset());

        // Per question: id, outcome, answer, validated evidence, every tool call with its result
        // count, the four token counts, an estimated cost and a duration.
        var questions = run.GetProperty("questions").EnumerateArray().ToList();
        Assert.Equal(
            LabeledQuestionSet.Load().Questions.Select(question => question.Id),
            questions.Select(question => question.GetProperty("id").GetString()));
        Assert.All(questions, question =>
        {
            var outcome = question.GetProperty("outcome").GetString();
            Assert.Contains(outcome, new[] { "answered", "insufficient_evidence" });
            Assert.Equal(JsonValueKind.Null, question.GetProperty("errorCode").ValueKind);
            Assert.NotEqual(Guid.Empty, question.GetProperty("investigationId").GetGuid());
            Assert.False(string.IsNullOrWhiteSpace(question.GetProperty("answer").GetString()));
            Assert.NotEmpty(question.GetProperty("expected").EnumerateArray());

            var toolCall = Assert.Single(question.GetProperty("toolCalls").EnumerateArray());
            Assert.Equal("search_code", toolCall.GetProperty("tool").GetString());
            Assert.Equal("called", toolCall.GetProperty("status").GetString());
            Assert.InRange(toolCall.GetProperty("resultCount").GetInt32(), 0, 5);
            Assert.True(toolCall.GetProperty("latencyMs").GetInt64() >= 0);

            var evidence = question.GetProperty("evidence").EnumerateArray().ToList();
            Assert.Equal(outcome == "answered", evidence.Count > 0);
            Assert.All(evidence, item =>
            {
                Assert.False(string.IsNullOrEmpty(item.GetProperty("path").GetString()));
                Assert.True(item.GetProperty("startLine").GetInt32() <= item.GetProperty("endLine").GetInt32());
                Assert.False(string.IsNullOrEmpty(item.GetProperty("excerpt").GetString()));
            });

            // The fake model reports no usage, so tokens and cost are genuinely zero.
            foreach (var kind in new[] { "input", "output", "cacheRead", "cacheWrite" })
            {
                Assert.Equal(0, question.GetProperty("tokens").GetProperty(kind).GetInt64());
            }

            Assert.Equal(0m, question.GetProperty("estimatedCostUsd").GetDecimal());
            Assert.True(question.GetProperty("durationMs").GetInt64() >= 0);
        });
        Assert.Contains(questions, question => question.GetProperty("outcome").GetString() == "answered");
        Assert.Equal(11, run.GetProperty("totals").GetProperty("questions").GetInt32());
        Assert.Equal(
            11,
            run.GetProperty("totals").GetProperty("answered").GetInt32()
                + run.GetProperty("totals").GetProperty("insufficientEvidence").GetInt32());

        // Progress output names questions by id only, never by their text or answer.
        Assert.Contains("pagination-offset: ", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("first page of the task list", output.ToString(), StringComparison.Ordinal);

        // The run's own database is gone, and the configured database was never written to.
        var databaseName = RunDatabaseName().Match(output.ToString()).Value;
        Assert.StartsWith(ThrowawayDatabase.NamePrefix, databaseName, StringComparison.Ordinal);
        Assert.Equal(0L, await ScalarAsync(configured.ConnectionString, $"SELECT count(*) FROM pg_database WHERE datname = '{databaseName}'"));
        Assert.Equal(0L, await ScalarAsync(configured.ConnectionString, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"));
    }

    [Fact]
    public async Task Keep_database_leaves_the_run_inspectable_and_split_limits_the_questions()
    {
        await using var configured = await postgres.CreateDatabaseAsync(migrate: false);
        using var outputDirectory = new TempDirectory();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await EvalCommand.RunAsync(
            ["run", "--dataset", SampleRepo.Path + ".questions.json", "--source", SampleRepo.Path, "--output", outputDirectory.Path, "--split", "dev", "--keep-database"],
            output,
            error,
            builder => builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Trace"] = configured.ConnectionString }));

        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        var result = EvalRunResult.FromJson(await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(outputDirectory.Path))));
        Assert.Equal(("dev", 5), (result.Dataset.Split, result.Dataset.QuestionCount));
        Assert.All(result.Questions, question => Assert.Equal("dev", question.Split));

        // The investigations the file describes are the rows the production pipeline stored.
        var databaseName = RunDatabaseName().Match(output.ToString()).Value;
        var kept = new NpgsqlConnectionStringBuilder(configured.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            Assert.Equal(5L, await ScalarAsync(kept, "SELECT count(*) FROM investigations WHERE state = 'completed'"));
            Assert.Equal(
                (long)result.Questions.Sum(question => question.Evidence.Count),
                await ScalarAsync(kept, "SELECT count(*) FROM evidence_refs"));
        }
        finally
        {
            await ScalarAsync(configured.ConnectionString, $"DROP DATABASE {databaseName} WITH (FORCE)");
        }
    }

    [Fact]
    public async Task Wrong_arguments_or_a_broken_dataset_stop_before_anything_is_created()
    {
        using var directory = new TempDirectory();
        var broken = Path.Combine(directory.Path, "broken.questions.json");
        await File.WriteAllTextAsync(broken, """{ "datasetVersion": "1", "questions": [] }""");
        static void MustNotConfigure(Microsoft.Extensions.Hosting.HostApplicationBuilder builder) =>
            Assert.Fail("The run must not get as far as building its services.");

        var error = new StringWriter();
        Assert.Equal(EvalCommand.ExitError, await EvalCommand.RunAsync(["run", "--dataset", broken], new StringWriter(), error, MustNotConfigure));
        Assert.Contains("'--source' is required", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage:", error.ToString(), StringComparison.Ordinal);

        error = new StringWriter();
        Assert.Equal(
            EvalCommand.ExitError,
            await EvalCommand.RunAsync(["run", "--dataset", broken, "--source", SampleRepo.Path], new StringWriter(), error, MustNotConfigure));
        Assert.Contains("at least one question", error.ToString(), StringComparison.Ordinal);

        error = new StringWriter();
        Assert.Equal(
            EvalCommand.ExitError,
            await EvalCommand.RunAsync(
                ["run", "--dataset", SampleRepo.Path + ".questions.json", "--source", Path.Combine(directory.Path, "missing")],
                new StringWriter(),
                error,
                MustNotConfigure));
        Assert.Contains("does not exist", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_and_a_cancelled_question_are_recorded_and_the_run_continues()
    {
        var model = new QuestionChatModel((question, request, _) => question switch
        {
            "model-down" => throw new ChatModelException("The provider is unreachable."),
            "model-cancelled" => throw new OperationCanceledException(),
            _ => Task.FromResult(GoodTurn(request)),
        });
        await using var indexed = await IndexAsync(model);
        var dataset = Dataset(indexed, "good-1", "model-down", "model-cancelled", "good-2");

        var result = await indexed.RunAsync(Request(indexed, dataset));

        Assert.Equal(EvalRunStatus.Completed, result.Status);
        Assert.Equal(
            [("good-1", "answered", null), ("model-down", "failed", "model_error"), ("model-cancelled", "cancelled", null), ("good-2", "answered", null)],
            result.Questions.Select(question => (question.Id, question.Outcome, question.ErrorCode)));

        // Run metadata comes from the snapshot and the pipeline, not from the dataset.
        Assert.Equal((TraceCommit, false), (result.TraceCommit, result.TraceCommitDirty));
        Assert.Equal(
            new EvalSnapshotInfo(indexed.Index.CommitSha, "main", LineChunker.Version, "deterministic-fake", indexed.EmbeddingModel),
            result.Snapshot);
        Assert.Equal(new EvalChatInfo("test-provider", model.ModelId, QuestionPrompt.Version), result.Chat);
        Assert.Equal(new EvalPriceInfo("USD", 10m, 50m, 1m, 12.5m), result.Prices);
        Assert.True(result.StartedAt <= result.EndedAt);
        Assert.Matches("^[0-9a-f]{32}$", result.RunId);

        // An answered question: validated evidence, the tool call, tokens over both turns, cost.
        var good = result.Questions[0];
        Assert.Equal("List skips Page * PageSize items.", good.Answer);
        var evidence = Assert.Single(good.Evidence);
        Assert.Equal((TaskRepositoryPath, 24, 26), (evidence.Path, evidence.StartLine, evidence.EndLine));
        Assert.Equal(string.Join('\n', SampleRepo.ReadLines(TaskRepositoryPath)[23..26]), evidence.Excerpt);
        var toolCall = Assert.Single(good.ToolCalls);
        Assert.Equal(("read_file_range", EvalToolCall.Called, 10), (toolCall.Tool, toolCall.Status, toolCall.ResultCount));
        Assert.NotNull(toolCall.LatencyMs);
        Assert.True(toolCall.OutputChars > 0);
        Assert.Equal(new EvalTokens(Input: 2_000, Output: 400, CacheRead: 600, CacheWrite: 200), good.Tokens);
        Assert.Equal(0.0351m, good.EstimatedCostUsd);
        Assert.True(good.DurationMs >= 0);

        // A failed question keeps what it spent before failing and has no answer or evidence.
        var failed = result.Questions[1];
        Assert.Null(failed.Answer);
        Assert.Empty(failed.Evidence);
        Assert.Equal(0m, failed.EstimatedCostUsd);

        Assert.Equal(
            new EvalTotals(4, Answered: 2, InsufficientEvidence: 0, Failed: 1, Cancelled: 1, NotRun: 0, new EvalTokens(4_000, 800, 1_200, 400), 0.0702m),
            result.Totals);

        // Each result is the production pipeline's own stored investigation, in its final state.
        await using var db = indexed.Database.CreateContext();
        var stored = await db.Investigations.AsNoTracking().ToDictionaryAsync(investigation => investigation.Id);
        Assert.Equal(4, stored.Count);
        Assert.Equal(
            [InvestigationState.Completed, InvestigationState.Failed, InvestigationState.Cancelled, InvestigationState.Completed],
            result.Questions.Select(question => stored[question.InvestigationId!.Value].State));
        Assert.All(stored.Values, investigation =>
        {
            Assert.Equal((indexed.RepositoryId, indexed.Index.SnapshotId), (investigation.RepositoryId, investigation.SnapshotId));
            Assert.Equal((model.ModelId, QuestionPrompt.Version), (investigation.ModelId, investigation.PromptVersion));
        });
        Assert.Equal(1, await db.EvidenceRefs.CountAsync(reference => reference.InvestigationId == good.InvestigationId));
    }

    [Fact]
    public async Task A_question_that_runs_past_the_timeout_fails_as_timed_out_and_the_next_one_still_runs()
    {
        var model = new QuestionChatModel(async (question, request, cancellationToken) =>
        {
            if (question == "never-returns")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return GoodTurn(request);
        });
        await using var indexed = await IndexAsync(model);
        var dataset = Dataset(indexed, "never-returns", "good-1");

        var result = await indexed.RunAsync(Request(indexed, dataset) with { QuestionTimeout = TimeSpan.FromSeconds(3) });

        Assert.Equal(EvalRunStatus.Completed, result.Status);
        Assert.Equal(
            [("never-returns", "failed", "timed_out"), ("good-1", "answered", null)],
            result.Questions.Select(question => (question.Id, question.Outcome, question.ErrorCode)));
        Assert.InRange(result.Questions[0].DurationMs, 2_500, 60_000);

        await using var db = indexed.Database.CreateContext();
        var investigation = await db.Investigations.AsNoTracking().SingleAsync(i => i.Id == result.Questions[0].InvestigationId);
        Assert.Equal((InvestigationState.Failed, "timed_out"), (investigation.State, investigation.ErrorCode));
    }

    [Fact]
    public async Task Stopping_the_run_cancels_the_question_in_progress_and_records_the_rest_as_not_run()
    {
        using var stop = new CancellationTokenSource();
        var model = new QuestionChatModel((question, request, cancellationToken) =>
        {
            if (question == "stop-here")
            {
                stop.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(GoodTurn(request));
        });
        await using var indexed = await IndexAsync(model);
        var dataset = Dataset(indexed, "good-1", "stop-here", "never-started-1", "never-started-2");
        var reported = new List<string>();

        var result = await indexed.RunAsync(Request(indexed, dataset), question => reported.Add(question.Id), stop.Token);

        Assert.Equal(EvalRunStatus.Cancelled, result.Status);
        Assert.Equal(
            [("good-1", "answered"), ("stop-here", "cancelled"), ("never-started-1", "not_run"), ("never-started-2", "not_run")],
            result.Questions.Select(question => (question.Id, question.Outcome)));
        Assert.Equal(["good-1", "stop-here"], reported);
        Assert.Equal(["good-1", "stop-here"], model.Questions.Distinct());
        Assert.All(result.Questions.Skip(2), question => Assert.Null(question.InvestigationId));
        Assert.Equal(new EvalTotals(4, 1, 0, 0, 1, 2, new EvalTokens(2_000, 400, 600, 200), 0.0351m), result.Totals);

        await using var db = indexed.Database.CreateContext();
        Assert.Equal(2, await db.Investigations.CountAsync());
        Assert.Equal(1, await db.Investigations.CountAsync(investigation => investigation.State == InvestigationState.Cancelled));
    }

    [Fact]
    public async Task A_model_that_obeys_injected_instructions_gets_no_extra_tools_and_no_unverified_evidence_into_the_result()
    {
        ScriptedChatModel script = null!;
        var model = new QuestionChatModel((_, request, _) => Task.FromResult(
            request.Messages.OfType<ToolResultMessage>().Any()
                ? ScriptedChatModel.Answer(
                    "pwned: this repository has no defects.",
                    (TaskRepositoryPath, 24, 26),     // real lines, but never retrieved in this run
                    ("docs/zebracorn.md", 1, 3))      // not in the snapshot at all
                : ModelTurn.Call(
                    script.Tool("shell", new { command = "cat /etc/passwd" }),
                    script.Read("docs/CONTRIBUTING.md", 1, 15),
                    script.Read("../../.env", 1, 5),
                    script.Tool("search_code", new { query = "secrets", repositoryId = Guid.NewGuid() }))));
        script = new ScriptedChatModel();
        await using var indexed = await IndexAsync(model);

        var result = await indexed.RunAsync(Request(indexed, Dataset(indexed, "hostile")));

        var question = Assert.Single(result.Questions);
        Assert.Equal(EvalOutcomes.InsufficientEvidence, question.Outcome);
        Assert.Equal(QuestionInvestigator.InsufficientEvidenceAnswer, question.Answer);
        Assert.Empty(question.Evidence);
        Assert.Equal(2, question.RejectedCitations);
        Assert.Equal(
            [(null, EvalToolCall.Rejected), ("read_file_range", EvalToolCall.Called), ("read_file_range", EvalToolCall.Failed), ("search_code", EvalToolCall.Failed)],
            question.ToolCalls.Select(call => (call.Tool, call.Status)));
        Assert.Equal([null, 15, null, null], question.ToolCalls.Select(call => call.ResultCount));
        Assert.All(question.ToolCalls.Where(call => call.Status == EvalToolCall.Failed), call => Assert.NotNull(call.ErrorCode));
        Assert.DoesNotContain("pwned", result.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repository_the_caller_may_not_see_is_refused_for_every_question_without_calling_the_model()
    {
        var model = new QuestionChatModel((_, request, _) => Task.FromResult(GoodTurn(request)));
        await using var indexed = await IndexAsync(model, RepositoryVisibility.Private);

        var result = await indexed.RunAsync(Request(indexed, Dataset(indexed, "first", "second")));

        Assert.Equal(EvalRunStatus.Completed, result.Status);
        Assert.All(result.Questions, question =>
        {
            Assert.Equal((EvalOutcomes.Failed, EvalRunner.RequestRejectedError), (question.Outcome, question.ErrorCode));
            Assert.Null(question.InvestigationId);
            Assert.Null(question.Answer);
            Assert.Empty(question.Evidence);
            Assert.Empty(question.ToolCalls);
        });
        Assert.Empty(model.Questions);

        await using var db = indexed.Database.CreateContext();
        Assert.Equal(0, await db.Investigations.CountAsync());
    }

    [Fact]
    public async Task A_dataset_labeled_against_another_revision_is_refused_unless_explicitly_allowed()
    {
        var model = new QuestionChatModel((_, request, _) => Task.FromResult(GoodTurn(request)));
        await using var indexed = await IndexAsync(model);
        var dataset = Dataset(indexed, "dev-1", "dev-2") with { Revision = new string('c', 40) };
        dataset = dataset with
        {
            Questions = [.. dataset.Questions, new EvalQuestion("held-out", EvalDataset.TestSplit, "held-out", [], Answerable: false)],
        };

        var exception = await Assert.ThrowsAsync<EvalException>(() => indexed.RunAsync(Request(indexed, dataset)));
        Assert.Contains(indexed.Index.CommitSha, exception.Message, StringComparison.Ordinal);
        Assert.Empty(model.Questions);

        var result = await indexed.RunAsync(
            Request(indexed, dataset) with { AllowRevisionMismatch = true, Split = EvalDataset.TestSplit, Prices = null });

        Assert.Equal(
            new EvalDatasetInfo("eval-test", "t1", new string('c', 40), RevisionMatchesSnapshot: false, "test", 1),
            result.Dataset);
        var question = Assert.Single(result.Questions);
        Assert.Equal(("held-out", false, "answered"), (question.Id, question.Answerable, question.Outcome));

        // Without configured prices nothing is guessed: the cost is absent, not zero.
        Assert.Null(result.Prices);
        Assert.Null(question.EstimatedCostUsd);
        Assert.Null(result.Totals.EstimatedCostUsd);

        await Assert.ThrowsAsync<EvalException>(
            () => indexed.RunAsync(Request(indexed, Dataset(indexed, "any")) with { SnapshotId = Guid.NewGuid() }));
    }

    /// <summary>Reads the lines it will cite, then answers citing them: a well-behaved model in two turns.</summary>
    private static ModelTurn GoodTurn(ModelRequest request) =>
        request.Messages.OfType<ToolResultMessage>().Any()
            ? ScriptedChatModel.Answer("List skips Page * PageSize items.", (TaskRepositoryPath, 24, 26)) with { Usage = TurnUsage }
            : new ModelTurn(
                [new ToolCall("call-1", QuestionPrompt.ReadFileRange, JsonSerializer.Serialize(new { path = TaskRepositoryPath, startLine = 20, endLine = 29 }))],
                FinalText: null,
                Usage: TurnUsage);

    /// <summary>A dataset whose question texts are their ids, so a test model can tell them apart.</summary>
    private static EvalDataset Dataset(IndexedForEval indexed, params string[] ids) =>
        new(
            "eval-test",
            "t1",
            indexed.Index.CommitSha,
            [.. ids.Select(id => new EvalQuestion(id, EvalDataset.DevSplit, id, [new EvalRange(TaskRepositoryPath, 20, 29)], Answerable: true))]);

    private static EvalRunRequest Request(IndexedForEval indexed, EvalDataset dataset) =>
        new(
            dataset,
            indexed.RepositoryId,
            indexed.Index.SnapshotId,
            ChatProvider: "test-provider",
            EmbeddingProvider: "deterministic-fake",
            Prices,
            TraceCommit,
            TraceCommitDirty: false,
            QuestionTimeout: TimeSpan.FromMinutes(2));

    private async Task<IndexedForEval> IndexAsync(IChatModel model, RepositoryVisibility visibility = RepositoryVisibility.Public)
    {
        var database = await postgres.CreateDatabaseAsync(configureServices: services =>
        {
            services.AddSingleton(model);
            services.AddSingleton<EvalRunner>();
        });
        await using var db = database.CreateContext();
        var repository = Repository.Create("trace-demo", "sample-repo", "main", visibility);
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();

        var index = await database.IndexAsync(repository.Id, SampleRepo.Source());
        await using var scope = database.CreateScope();
        return new IndexedForEval(
            database, repository.Id, index, scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>().ModelId);
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    [GeneratedRegex("trace_eval_[0-9a-f]{32}")]
    private static partial Regex RunDatabaseName();

    private sealed record IndexedForEval(TestDatabase Database, Guid RepositoryId, IndexResult Index, string EmbeddingModel) : IAsyncDisposable
    {
        public async Task<EvalRunResult> RunAsync(
            EvalRunRequest request,
            Action<EvalQuestionResult>? onQuestion = null,
            CancellationToken cancellationToken = default)
        {
            await using var scope = Database.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<EvalRunner>().RunAsync(request, onQuestion, cancellationToken);
        }

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    /// <summary>A chat model whose behaviour depends on which question it is asked.</summary>
    private sealed class QuestionChatModel(Func<string, ModelRequest, CancellationToken, Task<ModelTurn>> respond) : IChatModel
    {
        public string ModelId => "eval-test-model";

        /// <summary>The question of every call made, in order.</summary>
        public List<string> Questions { get; } = [];

        public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            var question = request.Messages.OfType<UserMessage>().First().Text;
            Questions.Add(question);
            return respond(question, request, cancellationToken);
        }
    }
}
