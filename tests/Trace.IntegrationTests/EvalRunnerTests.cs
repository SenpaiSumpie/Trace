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

    /// <summary>The checked-in smoke subset: <c>tests/fixtures/sample-repo.smoke.json</c>.</summary>
    private static readonly string SmokeExpectations = SampleRepo.Path + ".smoke.json";

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
        Assert.Equal(2, run.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("completed", run.GetProperty("status").GetString());
        Assert.Matches("^[0-9a-f]{40}$", run.GetProperty("traceCommit").GetString());
        Assert.Contains(run.GetProperty("traceCommitDirty").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.Equal("sample-repo", run.GetProperty("dataset").GetProperty("name").GetString());
        Assert.Equal("2", run.GetProperty("dataset").GetProperty("version").GetString());
        Assert.Equal("all", run.GetProperty("dataset").GetProperty("split").GetString());
        Assert.Equal(34, run.GetProperty("dataset").GetProperty("questionCount").GetInt32());
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

            // An unanswerable question carries its flag and no labeled location.
            Assert.Equal(
                question.GetProperty("answerable").GetBoolean(),
                question.GetProperty("expected").EnumerateArray().Any());

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

            // What recall is computed from: the search's ten best hits for the question, as
            // locations in files of the fixture, ranked from 1, with no source text.
            var retrieval = question.GetProperty("retrieval");
            Assert.Equal(10, retrieval.GetProperty("limit").GetInt32());
            Assert.Equal(JsonValueKind.Null, retrieval.GetProperty("errorCode").ValueKind);
            var hits = retrieval.GetProperty("hits").EnumerateArray().ToList();
            Assert.Equal(10, hits.Count);
            Assert.Equal(Enumerable.Range(1, 10), hits.Select(hit => hit.GetProperty("rank").GetInt32()));
            Assert.All(hits, hit =>
            {
                Assert.Equal(
                    ["rank", "path", "startLine", "endLine", "lexicalRank", "vectorRank"],
                    hit.EnumerateObject().Select(property => property.Name));
                var lines = SampleRepo.ReadLines(hit.GetProperty("path").GetString()!);
                Assert.InRange(hit.GetProperty("startLine").GetInt32(), 1, hit.GetProperty("endLine").GetInt32());
                Assert.InRange(hit.GetProperty("endLine").GetInt32(), 1, lines.Length);
            });
        });
        Assert.Contains(questions, question => question.GetProperty("outcome").GetString() == "answered");
        Assert.Equal(7, questions.Count(question => !question.GetProperty("answerable").GetBoolean()));
        Assert.Equal(34, run.GetProperty("totals").GetProperty("questions").GetInt32());
        Assert.Equal(
            34,
            run.GetProperty("totals").GetProperty("answered").GetInt32()
                + run.GetProperty("totals").GetProperty("insufficientEvidence").GetInt32());

        // The metrics come from that file alone. Recall at 10 with the fake embeddings is the
        // measured baseline for dataset version 2 (step M1.3: 34 questions, 27 of them
        // answerable with 31 labeled locations): 30 of 31 found, all 15 of the development
        // split's. It was 10 of 11 for version 1; the fixture and the question set grew, no
        // search setting changed.
        var reportOutput = new StringWriter();
        Assert.Equal(EvalCommand.ExitOk, await EvalCommand.RunAsync(["report", file], reportOutput, error));
        Assert.Equal(string.Empty, error.ToString());
        var report = EvalMetrics.Report(EvalRunResult.Load(file));
        var all = report.Metrics[0];
        Assert.Equal((31, 27, 0), (all.RecallAt10.Labeled, all.RecallAt10.QuestionsMeasured, all.RecallAt10.QuestionsNotMeasured));
        Assert.InRange(all.RecallAt10.Found, 30, 31);
        Assert.Equal((15, 19), (report.Metrics[1].Questions, report.Metrics[2].Questions));
        Assert.Equal(all.RecallAt10.Found, report.Metrics[1].RecallAt10.Found + report.Metrics[2].RecallAt10.Found);
        Assert.Equal((15, 15), (report.Metrics[1].RecallAt10.Found, report.Metrics[1].RecallAt10.Labeled));
        Assert.Equal(34, all.Duration.Questions);

        // The seven unanswerable questions are owed an abstention, in both splits. Whether they
        // get one is the model's doing (the fake chat model abstains only when its search finds
        // nothing), so the count owed is asserted here and the result is not.
        Assert.Equal(
            (7, 3, 4),
            (all.Abstention.Unanswerable, report.Metrics[1].Abstention.Unanswerable, report.Metrics[2].Abstention.Unanswerable));
        Assert.Equal(0m, all.Cost.TotalUsd);
        Assert.Contains("| Retrieval recall at 10 | ", reportOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("chat model is the deterministic fake", reportOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("first page of the task list", reportOutput.ToString(), StringComparison.Ordinal);

        // A run compared with itself has no differences.
        var compareOutput = new StringWriter();
        Assert.Equal(EvalCommand.ExitOk, await EvalCommand.RunAsync(["compare", file, file], compareOutput, error));
        Assert.Contains("0 regressed, 0 improved, 0 changed otherwise, 34 unchanged", compareOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(5, Directory.GetFiles(outputDirectory.Path).Length);

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
        Assert.Equal(("dev", 15), (result.Dataset.Split, result.Dataset.QuestionCount));
        Assert.All(result.Questions, question => Assert.Equal("dev", question.Split));

        // The investigations the file describes are the rows the production pipeline stored.
        var databaseName = RunDatabaseName().Match(output.ToString()).Value;
        var kept = new NpgsqlConnectionStringBuilder(configured.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            Assert.Equal(15L, await ScalarAsync(kept, "SELECT count(*) FROM investigations WHERE state = 'completed'"));
            Assert.Equal(
                (long)result.Questions.Sum(question => question.Evidence.Count),
                await ScalarAsync(kept, "SELECT count(*) FROM evidence_refs"));
        }
        finally
        {
            await ScalarAsync(configured.ConnectionString, $"DROP DATABASE {databaseName} WITH (FORCE)");
        }
    }

    /// <summary>
    /// Exactly what CI does (ADR-012): run the smoke subset with the fake providers, check the
    /// result file against the checked-in expectations, and write the report.
    /// </summary>
    [Fact]
    public async Task The_smoke_subset_runs_with_the_fake_providers_and_meets_its_expectations()
    {
        await using var configured = await postgres.CreateDatabaseAsync(migrate: false);
        using var outputDirectory = new TempDirectory();
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await EvalCommand.RunAsync(
            ["run", "--dataset", SampleRepo.Path + ".questions.json", "--source", SampleRepo.Path, "--output", outputDirectory.Path, "--expectations", SmokeExpectations],
            output,
            error,
            builder => builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Trace"] = configured.ConnectionString }));

        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        var file = Assert.Single(Directory.GetFiles(outputDirectory.Path));
        var result = EvalRunResult.Load(file);

        // Only the listed questions were asked, in dataset order, and the file says it is a subset.
        Assert.Equal(
            ["pagination-offset", "page-size-rationale", "title-max-length", "webhook-attempts", "api-key-rotation", "on-call-contact"],
            result.Questions.Select(question => question.Id));
        Assert.Equal((EvalDatasetInfo.SubsetSplit, 6), (result.Dataset.Split, result.Dataset.QuestionCount));
        Assert.Equal(("deterministic-fake", "deterministic-fake"), (result.Chat.Provider, result.Snapshot.EmbeddingProvider));
        Assert.All(result.Questions, question => Assert.Equal(EvalDataset.DevSplit, question.Split));

        var checkOutput = new StringWriter();
        exitCode = await EvalCommand.RunAsync(["check", file, "--expectations", SmokeExpectations], checkOutput, error);
        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        Assert.Contains("Check passed: 6 question(s) of 'sample-repo' meet their expectations.", checkOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());

        // The numbers behind the expectations: every label retrieved and cited, every citation valid.
        var metrics = EvalMetrics.Report(result).Metrics[0];
        Assert.Equal((7, 7), (metrics.RecallAt10.Found, metrics.RecallAt10.Labeled));
        Assert.Equal(1.0, metrics.CitationValidity.Value);
        Assert.Equal((5, 5), (metrics.LabeledEvidenceCited.Cited, metrics.LabeledEvidenceCited.Questions));

        var reportOutput = new StringWriter();
        Assert.Equal(EvalCommand.ExitOk, await EvalCommand.RunAsync(["report", file], reportOutput, error));
        Assert.Contains("only the questions an expectations file lists", reportOutput.ToString(), StringComparison.Ordinal);

        // The same result held against an expectation it does not meet fails the check. The
        // fake chat model answers the unanswerable question, so demanding an abstention breaks.
        var demanding = Path.Combine(outputDirectory.Path, "demanding.smoke.json");
        await File.WriteAllTextAsync(
            demanding,
            """{ "dataset": "sample-repo", "questions": [{ "id": "on-call-contact", "outcome": "insufficient_evidence" }] }""");
        var failed = new StringWriter();
        Assert.Equal(
            EvalCommand.ExitExpectationsNotMet,
            await EvalCommand.RunAsync(["check", file, "--expectations", demanding], new StringWriter(), failed));
        Assert.Contains(
            "on-call-contact: expected the outcome 'insufficient_evidence' but it was 'answered'.", failed.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The point of the smoke run: when the pipeline stops working, the run still finishes
    /// (a failed question is a measurement) and the check is what turns it into a failed build.
    /// </summary>
    [Fact]
    public async Task A_pipeline_that_stops_answering_fails_the_smoke_check()
    {
        await using var configured = await postgres.CreateDatabaseAsync(migrate: false);
        using var outputDirectory = new TempDirectory();
        var error = new StringWriter();
        var model = new QuestionChatModel((_, _, _) => throw new ChatModelException("The provider is unreachable."));

        var exitCode = await EvalCommand.RunAsync(
            ["run", "--dataset", SampleRepo.Path + ".questions.json", "--source", SampleRepo.Path, "--output", outputDirectory.Path, "--expectations", SmokeExpectations],
            new StringWriter(),
            error,
            builder =>
            {
                builder.Configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Trace"] = configured.ConnectionString });
                builder.Services.AddSingleton<IChatModel>(model);
            });

        Assert.True(exitCode == EvalCommand.ExitOk, $"Exit code {exitCode}. Errors: {error}");
        Assert.Equal(6, model.Questions.Count);
        var file = Assert.Single(Directory.GetFiles(outputDirectory.Path));

        var output = new StringWriter();
        var failed = new StringWriter();
        exitCode = await EvalCommand.RunAsync(["check", file, "--expectations", SmokeExpectations], output, failed);

        Assert.Equal((EvalCommand.ExitExpectationsNotMet, string.Empty), (exitCode, output.ToString()));
        Assert.Contains("Check FAILED: 11 expectation(s) not met for the 6 question(s) of 'sample-repo'.", failed.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "pagination-offset: expected the outcome 'answered' but it was 'failed (model_error)'.", failed.ToString(), StringComparison.Ordinal);
        Assert.Contains("pagination-offset: no validated evidence overlaps a labeled location.", failed.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "on-call-contact: expected an answer or an abstention but the outcome was 'failed (model_error)'.", failed.ToString(), StringComparison.Ordinal);

        // The search still works, so no retrieval expectation is reported: the failures point at the model.
        Assert.DoesNotContain("labeled location(s) are among", failed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expectations_that_name_a_held_out_or_unknown_question_stop_the_run_before_anything_is_created()
    {
        using var directory = new TempDirectory();
        static void MustNotConfigure(Microsoft.Extensions.Hosting.HostApplicationBuilder builder) =>
            Assert.Fail("The run must not get as far as building its services.");

        async Task<string> RunWithAsync(string expectationsJson)
        {
            var path = Path.Combine(directory.Path, "expectations.json");
            await File.WriteAllTextAsync(path, expectationsJson);
            var error = new StringWriter();
            Assert.Equal(
                EvalCommand.ExitError,
                await EvalCommand.RunAsync(
                    ["run", "--dataset", SampleRepo.Path + ".questions.json", "--source", SampleRepo.Path, "--output", directory.Path, "--expectations", path],
                    new StringWriter(),
                    error,
                    MustNotConfigure));
            return error.ToString();
        }

        // 'license' is a held-out question of the fixture.
        Assert.Contains(
            "Question 'license' is in the held-out 'test' split.",
            await RunWithAsync("""{ "dataset": "sample-repo", "questions": [{ "id": "pagination-offset" }, { "id": "license" }] }"""),
            StringComparison.Ordinal);
        Assert.Contains(
            "name question 'no-such-question', which dataset 'sample-repo' version 2 does not have",
            await RunWithAsync("""{ "dataset": "sample-repo", "questions": [{ "id": "no-such-question" }] }"""),
            StringComparison.Ordinal);
        Assert.Contains(
            "for dataset 'other-repo' but the dataset is 'sample-repo'",
            await RunWithAsync("""{ "dataset": "other-repo", "questions": [{ "id": "pagination-offset" }] }"""),
            StringComparison.Ordinal);
        Assert.Contains("is not a usable expectations file", await RunWithAsync("{ not json"), StringComparison.Ordinal);

        // Nothing but the expectations file itself was written.
        Assert.Single(Directory.GetFiles(directory.Path));
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
        Assert.Contains("'--source' or '--snapshot' is required", error.ToString(), StringComparison.Ordinal);
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

        // Retrieval is measured for every question the pipeline accepted, whatever the model
        // then did: it is the search asked the question itself, not anything the model asked for.
        Assert.All(result.Questions, question =>
        {
            Assert.NotNull(question.Retrieval);
            Assert.Equal((EvalRunner.RetrievalLimit, null), (question.Retrieval.Limit, question.Retrieval.ErrorCode));
            Assert.Equal(Enumerable.Range(1, question.Retrieval.Hits.Count), question.Retrieval.Hits.Select(hit => hit.Rank));
        });
        await using (var scope = indexed.Database.CreateScope())
        {
            var direct = await scope.ServiceProvider.GetRequiredService<Trace.Application.Retrieval.SourceTools>()
                .SearchCodeAsync(indexed.RepositoryId, indexed.Index.SnapshotId, "model-down", EvalRunner.RetrievalLimit);
            Assert.Equal(
                direct.Hits.Select(hit => (hit.Path, hit.StartLine, hit.EndLine, hit.LexicalRank, hit.VectorRank)),
                failed.Retrieval!.Hits.Select(hit => (hit.Path, hit.StartLine, hit.EndLine, hit.LexicalRank, hit.VectorRank)));
        }

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

        // Nothing is retrieved once the run has been stopped.
        Assert.NotNull(result.Questions[0].Retrieval);
        Assert.All(result.Questions.Skip(1), question => Assert.Null(question.Retrieval));
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

        // The recorded retrieval is the search for the dataset's question. Nothing the model
        // asked for or claimed is in it: not the file it made up, not the path it tried to escape with.
        Assert.NotNull(question.Retrieval);
        Assert.Null(question.Retrieval.ErrorCode);
        await using var db = indexed.Database.CreateContext();
        var indexedPaths = await db.SourceChunks.Select(chunk => chunk.Path).Distinct().ToListAsync();
        Assert.All(question.Retrieval.Hits, hit => Assert.Contains(hit.Path, indexedPaths));
        Assert.DoesNotContain("docs/zebracorn.md", indexedPaths);

        // Scored from the file: the two discarded citations count against citation validity.
        var metrics = EvalMetrics.Report(EvalRunResult.Parse(result.ToJson())).Metrics[0];
        Assert.Equal(new CitationMetric(0, Valid: 0, Cited: 2), metrics.CitationValidity);
        Assert.Equal(new LabeledEvidenceMetric(0, Cited: 0, Questions: 1), metrics.LabeledEvidenceCited);
    }

    [Fact]
    public async Task A_question_too_long_to_search_for_is_still_investigated_and_its_retrieval_is_recorded_as_not_measured()
    {
        var model = new QuestionChatModel((_, request, _) => Task.FromResult(GoodTurn(request)));
        await using var indexed = await IndexAsync(model);
        var longQuestion = string.Join(' ', Enumerable.Repeat("pagination", 150));
        Assert.InRange(longQuestion.Length, Trace.Application.Retrieval.SourceTools.MaxQueryLength + 1, EvalDataset.MaxQuestionLength);
        var dataset = Dataset(indexed, "short") with
        {
            Questions =
            [
                new EvalQuestion("too-long", EvalDataset.DevSplit, longQuestion, [new EvalRange(TaskRepositoryPath, 20, 29)], Answerable: true),
                new EvalQuestion("short", EvalDataset.DevSplit, "short", [new EvalRange(TaskRepositoryPath, 20, 29)], Answerable: true),
            ],
        };

        var result = await indexed.RunAsync(Request(indexed, dataset));

        Assert.Equal(["answered", "answered"], result.Questions.Select(question => question.Outcome));
        var tooLong = result.Questions[0].Retrieval;
        Assert.NotNull(tooLong);
        Assert.Equal((EvalRunner.RetrievalLimit, "invalid_argument"), (tooLong.Limit, tooLong.ErrorCode));
        Assert.Empty(tooLong.Hits);
        Assert.Null(result.Questions[1].Retrieval!.ErrorCode);

        // A retrieval that could not be made is "not measured", never a miss.
        var recall = EvalMetrics.Report(result).Metrics[0].RecallAt10;
        Assert.Equal((1, 1), (recall.QuestionsMeasured, recall.QuestionsNotMeasured));
        Assert.Equal(1, recall.Labeled);
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

            // The runner does not search a repository the pipeline refused to answer about.
            Assert.Null(question.Retrieval);
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
