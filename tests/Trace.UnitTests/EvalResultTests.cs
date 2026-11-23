using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Trace.Evals;

namespace Trace.UnitTests;

public class EvalResultTests
{
    [Fact]
    public void Cost_charges_cached_tokens_at_their_own_rates_and_the_rest_as_plain_input()
    {
        var prices = new ModelPrices(InputPerMillion: 10m, OutputPerMillion: 50m, CacheReadPerMillion: 1m, CacheWritePerMillion: 12.5m);

        // Input of 2,000 includes 600 read from the cache and 200 written to it: 1,200 are plain.
        // (1,200 x 10 + 400 x 50 + 600 x 1 + 200 x 12.5) / 1,000,000
        Assert.Equal(0.0351m, prices.Estimate(inputTokens: 2_000, outputTokens: 400, cacheReadTokens: 600, cacheWriteTokens: 200));
        Assert.Equal(0m, prices.Estimate(0, 0, 0, 0));
        Assert.Equal(60m, prices.Estimate(1_000_000, 1_000_000, 0, 0));
    }

    [Fact]
    public void Prices_come_from_configuration_per_model_and_are_absent_for_an_unlisted_model()
    {
        var configuration = Configuration(
            ("Eval:Prices:model-a:InputPerMillion", "3"),
            ("Eval:Prices:model-a:OutputPerMillion", "15.5"),
            ("Eval:Prices:model-a:CacheReadPerMillion", "0.3"),
            ("Eval:Prices:model-a:CacheWritePerMillion", "3.75"));

        Assert.Equal(new ModelPrices(3m, 15.5m, 0.3m, 3.75m), ModelPrices.FromConfiguration(configuration, "model-a"));
        Assert.Null(ModelPrices.FromConfiguration(configuration, "model-b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("cheap")]
    public void A_partly_configured_or_invalid_price_is_an_error_not_a_silent_zero(string? outputPrice)
    {
        var configuration = Configuration(
            ("Eval:Prices:model-a:InputPerMillion", "3"),
            ("Eval:Prices:model-a:OutputPerMillion", outputPrice),
            ("Eval:Prices:model-a:CacheReadPerMillion", "0.3"),
            ("Eval:Prices:model-a:CacheWritePerMillion", "3.75"));

        var exception = Assert.Throws<InvalidOperationException>(() => ModelPrices.FromConfiguration(configuration, "model-a"));

        Assert.Contains("Eval:Prices:model-a:OutputPerMillion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_settings_price_the_fake_model_and_the_default_chat_model_and_name_no_secret()
    {
        var path = Path.Combine(AppContext.BaseDirectory, EvalCommand.SettingsFile);
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        Assert.Equal(new ModelPrices(0, 0, 0, 0), ModelPrices.FromConfiguration(configuration, "fake-extractive-v1"));

        // The owner's figures for the baseline run (step M1.5), not confirmed against a bill:
        // cache read is input x 0.1 and cache write is input x 1.25.
        Assert.Equal(
            new ModelPrices(4m, 20m, 0.4m, 5m),
            ModelPrices.FromConfiguration(configuration, Trace.Infrastructure.Chat.AnthropicChatOptions.DefaultModel));
        Assert.Equal(
            ["claude-opus-5-5", "fake-extractive-v1"],
            configuration.GetSection(ModelPrices.Section).GetChildren().Select(child => child.Key).Order(StringComparer.Ordinal));
        Assert.Contains("not been confirmed", configuration["Eval:PricesNote"], StringComparison.Ordinal);
        Assert.Equal("deterministic-fake", configuration["Chat:Provider"]);
        Assert.Equal("deterministic-fake", configuration["Embeddings:Provider"]);
        Assert.DoesNotContain("ApiKey", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The result file's schema, property by property. A renamed, removed or added field fails
    /// here: change <see cref="EvalRunResult.CurrentSchemaVersion"/> and docs/evaluation.md with it.
    /// </summary>
    [Fact]
    public void Result_file_has_exactly_the_documented_fields()
    {
        using var document = JsonDocument.Parse(SampleResult().ToJson());
        var run = document.RootElement;

        Assert.Equal(2, run.GetProperty("schemaVersion").GetInt32());
        AssertProperties(
            run,
            "schemaVersion", "runId", "status", "traceCommit", "traceCommitDirty", "dataset", "snapshot", "chat", "prices",
            "startedAt", "endedAt", "totals", "questions");
        AssertProperties(run.GetProperty("dataset"), "name", "version", "revision", "revisionMatchesSnapshot", "split", "questionCount");
        AssertProperties(run.GetProperty("snapshot"), "commitSha", "branch", "chunkerVersion", "embeddingProvider", "embeddingModel");
        AssertProperties(run.GetProperty("chat"), "provider", "model", "promptVersion");
        AssertProperties(
            run.GetProperty("prices"), "currency", "inputPerMillion", "outputPerMillion", "cacheReadPerMillion", "cacheWritePerMillion");
        AssertProperties(
            run.GetProperty("totals"),
            "questions", "answered", "insufficientEvidence", "failed", "cancelled", "notRun", "tokens", "estimatedCostUsd");
        AssertProperties(run.GetProperty("totals").GetProperty("tokens"), "input", "output", "cacheRead", "cacheWrite");

        var question = run.GetProperty("questions")[0];
        AssertProperties(
            question,
            "id", "split", "question", "answerable", "expected", "retrieval", "outcome", "errorCode", "investigationId", "answer",
            "evidence", "unknowns", "limitations", "toolCalls", "rejectedCitations", "tokens", "estimatedCostUsd", "durationMs");
        AssertProperties(question.GetProperty("expected")[0], "path", "startLine", "endLine");

        // Version 2: where the search's hits for the question are. Locations only, no source text.
        AssertProperties(question.GetProperty("retrieval"), "limit", "hits", "errorCode");
        AssertProperties(
            question.GetProperty("retrieval").GetProperty("hits")[0], "rank", "path", "startLine", "endLine", "lexicalRank", "vectorRank");
        AssertProperties(question.GetProperty("evidence")[0], "path", "startLine", "endLine", "supports", "excerpt");
        AssertProperties(
            question.GetProperty("toolCalls")[0],
            "seq", "tool", "status", "resultCount", "latencyMs", "outputChars", "errorCode", "summary");
        AssertProperties(question.GetProperty("tokens"), "input", "output", "cacheRead", "cacheWrite");

        // Values a reader depends on: timestamps are ISO 8601 in UTC, money is a JSON number.
        Assert.Equal("2026-11-23T10:00:00+00:00", run.GetProperty("startedAt").GetString());
        Assert.Equal(0.0351m, question.GetProperty("estimatedCostUsd").GetDecimal());
        Assert.Equal(JsonValueKind.Null, question.GetProperty("errorCode").ValueKind);
    }

    [Fact]
    public void Result_survives_a_round_trip_and_keeps_source_text_readable()
    {
        var result = SampleResult();
        var json = result.ToJson();

        // Source excerpts are not turned into \uXXXX escapes.
        Assert.Contains("if (a < b && name == \\\"x\\\")", json, StringComparison.Ordinal);

        var read = EvalRunResult.FromJson(json);
        Assert.Equal(result.Questions[0].Evidence, read.Questions[0].Evidence);
        Assert.Equal(result.Questions[0].ToolCalls, read.Questions[0].ToolCalls);
        Assert.Equal(result.Questions[0].Retrieval!.Hits, read.Questions[0].Retrieval!.Hits);
        Assert.Equal(result with { Questions = [] }, read with { Questions = [] });

        // Reading a file for scoring accepts what the runner writes.
        Assert.Equal(result.Questions[0].Evidence, EvalRunResult.Parse(json).Questions[0].Evidence);
    }

    [Fact]
    public void A_version_1_file_is_still_read_and_has_no_retrieval()
    {
        // Exactly what the previous format wrote: no "retrieval" property at all.
        var json = SampleResult().ToJson()
            .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal);
        var withoutRetrieval = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        withoutRetrieval["questions"]![0]!.AsObject().Remove("retrieval");

        var read = EvalRunResult.Parse(withoutRetrieval.ToJsonString());

        Assert.Equal(1, read.SchemaVersion);
        Assert.Null(Assert.Single(read.Questions).Retrieval);
    }

    [Theory]
    [InlineData("", "not JSON")]
    [InlineData("null", "empty")]
    [InlineData("[]", "not JSON")]
    [InlineData("""{ "schemaVersion": 3, "runId": "r", "questions": [] }""", "schemaVersion is 3")]
    [InlineData("""{ "schemaVersion": 0, "runId": "r", "questions": [] }""", "schemaVersion is 0")]
    [InlineData("""{ "schemaVersion": 2, "questions": [] }""", "are required")]
    public void A_file_that_is_not_a_result_file_is_refused_with_the_reason(string json, string expectedMessage)
    {
        var exception = Assert.Throws<EvalResultFileException>(() => EvalRunResult.Parse(json));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"split\": \"dev\"", "\"split\": \"train\"", "\"split\"")]
    [InlineData("\"outcome\": \"answered\"", "\"outcome\": \"great\"", "unknown \"outcome\"")]
    [InlineData("\"id\": \"pagination-offset\"", "\"id\": \"\"", "needs an \"id\"")]
    public void A_question_a_metric_cannot_place_is_refused_rather_than_scored(string find, string replace, string expectedMessage)
    {
        var json = SampleResult().ToJson();
        Assert.Contains(find, json, StringComparison.Ordinal);

        var exception = Assert.Throws<EvalResultFileException>(
            () => EvalRunResult.Parse(json.Replace(find, replace, StringComparison.Ordinal)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_questions_with_one_id_or_a_missing_file_are_refused()
    {
        var sample = SampleResult();
        var twice = sample with { Questions = [sample.Questions[0], sample.Questions[0]] };
        Assert.Contains(
            "more than once",
            Assert.Throws<EvalResultFileException>(() => EvalRunResult.Parse(twice.ToJson())).Message,
            StringComparison.Ordinal);

        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json");
        Assert.Contains(
            "Could not read",
            Assert.Throws<EvalResultFileException>(() => EvalRunResult.Load(missing)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_left_out_of_a_hand_written_file_are_read_as_empty()
    {
        var read = EvalRunResult.Parse("""
            {
              "schemaVersion": 2, "runId": "r1", "status": "completed", "traceCommit": "unknown",
              "dataset": { "name": "d", "version": "1", "split": "all", "questionCount": 1 },
              "snapshot": { "commitSha": "c", "branch": "main", "chunkerVersion": "v", "embeddingProvider": "p", "embeddingModel": "m" },
              "chat": { "provider": "p", "model": "m", "promptVersion": "v" },
              "questions": [
                { "id": "q1", "split": "test", "answerable": false, "outcome": "not_run",
                  "tokens": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 }, "retrieval": { "limit": 10 } }
              ]
            }
            """);

        var question = Assert.Single(read.Questions);
        Assert.Empty(question.Expected);
        Assert.Empty(question.Evidence);
        Assert.Empty(question.ToolCalls);
        Assert.Empty(question.Retrieval!.Hits);
    }

    [Fact]
    public void Result_file_name_is_sortable_by_dataset_and_start_time()
    {
        Assert.Equal("sample-repo-20261123T100000Z-0123abcd.json", SampleResult().FileName());
    }

    [Fact]
    public void Default_output_directory_is_git_ignored()
    {
        var root = EvalCommand.FindRepositoryRoot(AppContext.BaseDirectory);
        Assert.NotNull(root);

        // Result files quote the evaluated source, which may be private: they must not be committable by default.
        Assert.Contains(EvalCommand.DefaultOutputDirectory + "/", File.ReadAllLines(Path.Combine(root, ".gitignore")));
        Assert.Contains("eval/local/", File.ReadAllLines(Path.Combine(root, ".gitignore")));
    }

    [Fact]
    public void Arguments_are_parsed_and_anything_unknown_is_refused()
    {
        var options = EvalCommand.ParseArguments(
            ["run", "--dataset", "d.json", "--source", "repo", "--output", "out", "--split", "dev", "--allow-revision-mismatch", "--keep-database"]);
        Assert.Equal(new EvalOptions("d.json", "repo", "out", "dev", AllowRevisionMismatch: true, KeepDatabase: true), options);

        Assert.Equal(
            new EvalOptions("d.json", "repo", null, null, AllowRevisionMismatch: false, KeepDatabase: false),
            EvalCommand.ParseArguments(["run", "--source", "repo", "--dataset", "d.json"]));

        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments([]));

        // 'report' and 'compare' have a parser of their own; this one is for 'run' only.
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["report"]));
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["run", "--source", "repo"]));
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["run", "--dataset", "d.json"]));
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["run", "--dataset"]));
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["run", "--dataset", "d", "--source", "s", "--split", "train"]));
        Assert.Throws<EvalUsageException>(() => EvalCommand.ParseArguments(["run", "--dataset", "d", "--source", "s", "--model", "x"]));
    }

    private static EvalRunResult SampleResult()
    {
        var tokens = new EvalTokens(Input: 2_000, Output: 400, CacheRead: 600, CacheWrite: 200);
        var question = new EvalQuestionResult(
            "pagination-offset",
            "dev",
            "Why does the first page skip results?",
            Answerable: true,
            [new EvalRange("src/A.cs", 20, 29)],
            new EvalRetrieval(
                Limit: 10,
                [new EvalRetrievedLocation(1, "src/A.cs", 1, 40, LexicalRank: 1, VectorRank: 2), new EvalRetrievedLocation(2, "docs/b.md", 1, 12, null, 1)],
                ErrorCode: null),
            EvalOutcomes.Answered,
            ErrorCode: null,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Because of the offset.",
            [new EvalEvidence("src/A.cs", 24, 26, "Shows the offset.", "if (a < b && name == \"x\")")],
            ["Runtime behaviour was not observed."],
            [],
            [new EvalToolCall(2, "search_code", EvalToolCall.Called, ResultCount: 5, LatencyMs: 12, OutputChars: 900, ErrorCode: null, "Searched.")],
            RejectedCitations: 0,
            tokens,
            EstimatedCostUsd: 0.0351m,
            DurationMs: 1_234);

        return new EvalRunResult(
            EvalRunResult.CurrentSchemaVersion,
            "0123abcd0123abcd0123abcd0123abcd",
            EvalRunStatus.Completed,
            new string('a', 40),
            TraceCommitDirty: false,
            new EvalDatasetInfo("sample-repo", "1", new string('b', 40), RevisionMatchesSnapshot: true, "all", 1),
            new EvalSnapshotInfo(new string('b', 40), "main", "lines-v1", "deterministic-fake", "fake-v1"),
            new EvalChatInfo("deterministic-fake", "fake-extractive-v1", "question-v2"),
            new EvalPriceInfo("USD", 10m, 50m, 1m, 12.5m),
            new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 23, 10, 0, 2, TimeSpan.Zero),
            new EvalTotals(1, 1, 0, 0, 0, 0, tokens, 0.0351m),
            [question]);
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => KeyValuePair.Create(setting.Key, setting.Value)))
            .Build();
}
