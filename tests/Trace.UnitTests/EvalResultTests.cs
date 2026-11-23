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
    public void The_shipped_settings_price_only_the_fake_model_and_name_no_secret()
    {
        var path = Path.Combine(AppContext.BaseDirectory, EvalCommand.SettingsFile);
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        Assert.Equal(new ModelPrices(0, 0, 0, 0), ModelPrices.FromConfiguration(configuration, "fake-extractive-v1"));
        Assert.Equal("fake-extractive-v1", Assert.Single(configuration.GetSection(ModelPrices.Section).GetChildren()).Key);
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

        Assert.Equal(1, run.GetProperty("schemaVersion").GetInt32());
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
            "id", "split", "question", "answerable", "expected", "outcome", "errorCode", "investigationId", "answer", "evidence",
            "unknowns", "limitations", "toolCalls", "rejectedCitations", "tokens", "estimatedCostUsd", "durationMs");
        AssertProperties(question.GetProperty("expected")[0], "path", "startLine", "endLine");
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
        Assert.Equal(result with { Questions = [] }, read with { Questions = [] });
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
