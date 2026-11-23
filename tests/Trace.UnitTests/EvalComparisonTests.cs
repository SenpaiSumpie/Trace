using System.Text.Json;
using Trace.Evals;

namespace Trace.UnitTests;

/// <summary>Comparing two hand-built result files, and the two commands that read result files.</summary>
public class EvalComparisonTests
{
    private static readonly (string, int, int)[] HitOnLabel = [("src/A.cs", 1, 40)];
    private static readonly (string, int, int)[] HitElsewhere = [("src/B.cs", 1, 40)];
    private static readonly (string, int, int)[] EvidenceOnLabel = [("src/A.cs", 12, 14)];
    private static readonly (string, int, int)[] EvidenceElsewhere = [("src/B.cs", 1, 2)];

    [Fact]
    public void Each_kind_of_regression_is_detected_and_named()
    {
        var baseline = EvalFiles.Run(
            EvalFiles.Question("lost-outcome", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("lost-recall", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("lost-labeled-evidence", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("less-valid-citations", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("answered-the-unanswerable", answerable: false, outcome: EvalOutcomes.InsufficientEvidence));
        var candidate = EvalFiles.Run(
            "fedcba9876543210fedcba9876543210",
            EvalFiles.Question("lost-outcome", hits: HitOnLabel, outcome: EvalOutcomes.Failed, errorCode: "model_error"),
            EvalFiles.Question("lost-recall", hits: HitElsewhere, evidence: EvidenceOnLabel),
            EvalFiles.Question("lost-labeled-evidence", hits: HitOnLabel, evidence: EvidenceElsewhere),
            EvalFiles.Question("less-valid-citations", hits: HitOnLabel, evidence: EvidenceOnLabel, rejectedCitations: 1),
            EvalFiles.Question("answered-the-unanswerable", answerable: false, outcome: EvalOutcomes.Answered, evidence: EvidenceElsewhere));

        var comparison = EvalComparer.Compare(baseline, candidate);

        Assert.Equal(new EvalChangeCounts(Regressed: 5, 0, 0, 0, 0, 0), comparison.Counts);
        Assert.All(comparison.Questions, change => Assert.Equal(EvalChangeStatus.Regressed, change.Status));
        Assert.Equal(
            [
                "outcome: answered -> failed (model_error)|no longer cites a labeled location",
                "recall at 10: 1 of 1 -> 0 of 1",
                "no longer cites a labeled location",
                "valid citations: 1 of 1 -> 1 of 2",
                "outcome: insufficient_evidence -> answered",
            ],
            comparison.Questions.Select(change => string.Join('|', change.Regressions)));
        Assert.All(comparison.Questions, change => Assert.Empty(change.Improvements));

        // The evidence files that came and went are listed, not only counted.
        var moved = comparison.Questions[2];
        Assert.Equal(["src/B.cs"], moved.EvidenceFilesAdded);
        Assert.Equal(["src/A.cs"], moved.EvidenceFilesRemoved);
        Assert.Equal(["evidence files: 1 added, 1 removed"], moved.OtherChanges);
    }

    [Fact]
    public void The_same_changes_in_the_other_direction_are_improvements()
    {
        var before = EvalFiles.Run(
            EvalFiles.Question("gained-outcome", hits: HitOnLabel, outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("gained-recall", hits: HitElsewhere, evidence: EvidenceOnLabel),
            EvalFiles.Question("more-valid-citations", hits: HitOnLabel, evidence: EvidenceOnLabel, rejectedCitations: 3));
        var after = EvalFiles.Run(
            EvalFiles.Question("gained-outcome", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("gained-recall", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("more-valid-citations", hits: HitOnLabel, evidence: EvidenceOnLabel, rejectedCitations: 1));

        var comparison = EvalComparer.Compare(before, after);

        Assert.Equal(new EvalChangeCounts(0, Improved: 3, 0, 0, 0, 0), comparison.Counts);
        Assert.Equal(
            [
                "outcome: insufficient_evidence -> answered|now cites a labeled location",
                "recall at 10: 0 of 1 -> 1 of 1",
                "valid citations: 1 of 4 -> 1 of 2",
            ],
            comparison.Questions.Select(change => string.Join('|', change.Improvements)));
    }

    [Fact]
    public void A_question_that_got_better_and_worse_counts_as_regressed_and_says_both()
    {
        var before = EvalFiles.Run(EvalFiles.Question("mixed", hits: HitElsewhere, evidence: EvidenceOnLabel));
        var after = EvalFiles.Run(EvalFiles.Question("mixed", hits: HitOnLabel, evidence: EvidenceElsewhere));

        var change = Assert.Single(EvalComparer.Compare(before, after).Questions);

        Assert.Equal(EvalChangeStatus.Regressed, change.Status);
        Assert.Equal(["no longer cites a labeled location"], change.Regressions);
        Assert.Equal(["recall at 10: 0 of 1 -> 1 of 1"], change.Improvements);
    }

    [Fact]
    public void Cost_and_time_are_reported_as_differences_but_never_make_a_question_changed()
    {
        var before = EvalFiles.Run(EvalFiles.Question("same", hits: HitOnLabel, evidence: EvidenceOnLabel, cost: 0.10m, durationMs: 1_000));
        var after = EvalFiles.Run(EvalFiles.Question("same", hits: HitOnLabel, evidence: EvidenceOnLabel, cost: 0.25m, durationMs: 400));

        var change = Assert.Single(EvalComparer.Compare(before, after).Questions);

        Assert.Equal(EvalChangeStatus.Unchanged, change.Status);
        Assert.Equal(0.15m, change.CostDeltaUsd);
        Assert.Equal(-600L, change.DurationDeltaMs);
        Assert.Empty(change.Regressions);
        Assert.Empty(change.Improvements);
        Assert.Empty(change.OtherChanges);
    }

    [Fact]
    public void Differences_that_are_neither_better_nor_worse_are_changed_and_have_no_deltas_unless_both_ran_to_an_end()
    {
        var before = EvalFiles.Run(
            EvalFiles.Question("other-failure", outcome: EvalOutcomes.Failed, errorCode: "model_error"),
            EvalFiles.Question("other-file", hits: HitOnLabel, evidence: [("src/A.cs", 12, 14), ("src/B.cs", 1, 2)]),
            EvalFiles.Question("now-cancelled", outcome: EvalOutcomes.Failed, errorCode: "model_error", cost: 0.1m),
            EvalFiles.Question("recall-now-measured", evidence: EvidenceOnLabel));
        var after = EvalFiles.Run(
            EvalFiles.Question("other-failure", outcome: EvalOutcomes.Failed, errorCode: "timed_out"),
            EvalFiles.Question("other-file", hits: HitOnLabel, evidence: [("src/A.cs", 12, 14), ("src/C.cs", 1, 2)]),
            EvalFiles.Question("now-cancelled", outcome: EvalOutcomes.Cancelled, cost: 0.3m),
            EvalFiles.Question("recall-now-measured", hits: HitOnLabel, evidence: EvidenceOnLabel));

        var comparison = EvalComparer.Compare(before, after);

        Assert.Equal(new EvalChangeCounts(0, 0, Changed: 4, 0, 0, 0), comparison.Counts);
        Assert.Equal(
            [
                "outcome: failed (model_error) -> failed (timed_out)",
                "evidence files: 1 added, 1 removed",
                "outcome: failed (model_error) -> cancelled",
                "recall at 10 is measured only in the candidate",
            ],
            comparison.Questions.Select(change => string.Join('|', change.OtherChanges)));
        Assert.Null(comparison.Questions[2].CostDeltaUsd);
        Assert.Null(comparison.Questions[2].DurationDeltaMs);
    }

    [Fact]
    public void Regressions_come_first_then_improvements_changes_missing_questions_and_unchanged_ones_each_in_file_order()
    {
        var before = EvalFiles.Run(
            EvalFiles.Question("unchanged-1", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("improved", hits: HitElsewhere, evidence: EvidenceOnLabel),
            EvalFiles.Question("removed", hits: HitOnLabel),
            EvalFiles.Question("regressed-1", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("changed", outcome: EvalOutcomes.Failed, errorCode: "model_error"),
            EvalFiles.Question("unchanged-2", "test", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("regressed-2", "test", hits: HitOnLabel, evidence: EvidenceOnLabel));
        var after = EvalFiles.Run(
            EvalFiles.Question("added", hits: HitOnLabel),
            EvalFiles.Question("regressed-2", "test", hits: HitElsewhere, evidence: EvidenceOnLabel),
            EvalFiles.Question("unchanged-2", "test", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("changed", outcome: EvalOutcomes.Failed, errorCode: "timed_out"),
            EvalFiles.Question("regressed-1", hits: HitOnLabel, outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("improved", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("unchanged-1", hits: HitOnLabel, evidence: EvidenceOnLabel));

        var comparison = EvalComparer.Compare(before, after);

        Assert.Equal(
            [
                ("regressed-1", EvalChangeStatus.Regressed),
                ("regressed-2", EvalChangeStatus.Regressed),
                ("improved", EvalChangeStatus.Improved),
                ("changed", EvalChangeStatus.Changed),
                ("removed", EvalChangeStatus.OnlyInBaseline),
                ("added", EvalChangeStatus.OnlyInCandidate),
                ("unchanged-1", EvalChangeStatus.Unchanged),
                ("unchanged-2", EvalChangeStatus.Unchanged),
            ],
            comparison.Questions.Select(change => (change.Id, change.Status)));
        Assert.Equal(new EvalChangeCounts(2, 1, 1, 1, 1, 2), comparison.Counts);

        var removed = comparison.Questions[4];
        Assert.NotNull(removed.Baseline);
        Assert.Null(removed.Candidate);
        Assert.Null(comparison.Questions[5].Baseline);

        // Both runs' metrics are there in full, overall and per split.
        Assert.Equal(["all", "dev", "test"], comparison.BaselineMetrics.Select(set => set.Scope));
        Assert.Equal((7, 2), (comparison.BaselineMetrics[0].Questions, comparison.CandidateMetrics[2].Questions));

        var markdown = EvalMarkdown.Comparison(comparison);
        Assert.Contains("2 regressed, 1 improved, 1 changed otherwise, 2 unchanged, 1 only in the baseline, 1 only in the candidate.", markdown, StringComparison.Ordinal);
        Assert.Contains("| `regressed-1` | `dev` | **regressed** | `answered` -> `insufficient_evidence` | 1 of 1 | yes -> no | 1 of 1 -> 0 of 0 | $0.0000 | 100 ms |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `removed` | `dev` | only in baseline | `answered` -> absent |", markdown, StringComparison.Ordinal);
        Assert.Contains("### `regressed-2`: **regressed**", markdown, StringComparison.Ordinal);
        Assert.Contains("- Worse: `recall at 10: 1 of 1 -> 0 of 1`", markdown, StringComparison.Ordinal);
        Assert.Contains("### All questions", markdown, StringComparison.Ordinal);
        Assert.Contains("### Development split (`dev`)", markdown, StringComparison.Ordinal);
        Assert.Contains("### Held-out split (`test`)", markdown, StringComparison.Ordinal);
        Assert.True(
            markdown.IndexOf("| `regressed-1` |", StringComparison.Ordinal) < markdown.IndexOf("| `improved` |", StringComparison.Ordinal)
            && markdown.IndexOf("| `improved` |", StringComparison.Ordinal) < markdown.IndexOf("| `unchanged-1` |", StringComparison.Ordinal));
        Assert.DoesNotContain(EvalFiles.QuestionText, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalFiles.AnswerText, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalFiles.ExcerptText, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Runs_that_are_not_like_for_like_are_flagged()
    {
        var baseline = EvalFiles.Run(EvalFiles.Question("q", hits: HitOnLabel));
        Assert.Empty(EvalComparer.Compare(baseline, baseline).Warnings);

        var candidate = EvalFiles.Run(
            EvalFiles.Question("q", hits: HitOnLabel, expected: [new EvalRange("src/A.cs", 1, 5)])) with
        {
            SchemaVersion = 1,
            Status = EvalRunStatus.Cancelled,
            Dataset = new EvalDatasetInfo("sample-repo", "2", null, null, "dev", 1),
            Snapshot = new EvalSnapshotInfo(new string('c', 40), "main", "lines-v1", "deterministic-fake", "fake-v1"),
        };

        var warnings = string.Join('\n', EvalComparer.Compare(baseline, candidate).Warnings);

        Assert.Contains("different datasets or dataset versions", warnings, StringComparison.Ordinal);
        Assert.Contains("different splits", warnings, StringComparison.Ordinal);
        Assert.Contains("different revisions of the source", warnings, StringComparison.Ordinal);
        Assert.Contains("stopped early", warnings, StringComparison.Ordinal);
        Assert.Contains("schema version 1", warnings, StringComparison.Ordinal);
        Assert.Contains("1 question(s) have different text or labels", warnings, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_empty_runs_and_two_all_failed_runs_compare_without_any_change()
    {
        var empty = EvalComparer.Compare(EvalFiles.Run(), EvalFiles.Run());
        Assert.Equal(new EvalChangeCounts(0, 0, 0, 0, 0, 0), empty.Counts);
        Assert.Empty(empty.Questions);
        Assert.Contains("Neither file has any question.", EvalMarkdown.Comparison(empty), StringComparison.Ordinal);

        var failed = EvalFiles.Run(
            EvalFiles.Question("a", outcome: EvalOutcomes.Failed, errorCode: "model_error"),
            EvalFiles.Question("b", answerable: false, outcome: EvalOutcomes.Failed, errorCode: "model_error"));
        var comparison = EvalComparer.Compare(failed, failed);
        Assert.Equal(new EvalChangeCounts(0, 0, 0, 0, 0, Unchanged: 2), comparison.Counts);
        Assert.DoesNotContain("NaN", EvalMarkdown.Comparison(comparison), StringComparison.Ordinal);
    }

    [Fact]
    public void The_comparison_json_has_exactly_the_documented_fields()
    {
        var before = EvalFiles.Run(EvalFiles.Question("q", hits: HitOnLabel, evidence: EvidenceOnLabel));
        var after = EvalFiles.Run(EvalFiles.Question("q", hits: HitElsewhere, evidence: EvidenceOnLabel));
        using var document = JsonDocument.Parse(EvalComparer.Compare(before, after).ToJson());
        var comparison = document.RootElement;

        Assert.Equal(1, comparison.GetProperty("comparisonVersion").GetInt32());
        AssertProperties(
            comparison,
            "comparisonVersion", "baseline", "candidate", "warnings", "counts", "baselineMetrics", "candidateMetrics", "questions");
        AssertProperties(
            comparison.GetProperty("counts"), "regressed", "improved", "changed", "onlyInBaseline", "onlyInCandidate", "unchanged");
        var question = comparison.GetProperty("questions")[0];
        AssertProperties(
            question,
            "id", "split", "status", "regressions", "improvements", "otherChanges", "baseline", "candidate", "evidenceFilesAdded",
            "evidenceFilesRemoved", "costDeltaUsd", "durationDeltaMs");
        Assert.Equal("regressed", question.GetProperty("status").GetString());
        Assert.Equal(1, question.GetProperty("baseline").GetProperty("recallFound").GetInt32());
        Assert.Equal(0, question.GetProperty("candidate").GetProperty("recallFound").GetInt32());
    }

    [Fact]
    public async Task The_report_command_prints_the_metrics_and_writes_markdown_and_json_next_to_the_result_file()
    {
        using var directory = new ScratchDirectory();
        var resultFile = await EvalFiles.Run(
            EvalFiles.Question("dev-1", "dev", hits: HitOnLabel, evidence: EvidenceOnLabel),
            EvalFiles.Question("test-1", "test", answerable: false, outcome: EvalOutcomes.InsufficientEvidence)).WriteAsync(directory.Path);
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await EvalCommand.RunAsync(["report", resultFile], output, error, MustNotBuildServices);

        Assert.Equal((EvalCommand.ExitOk, string.Empty), (exitCode, error.ToString()));
        var markdownPath = Path.Combine(directory.Path, "sample-repo-20261123T100000Z-0123abcd.report.md");
        var jsonPath = Path.Combine(directory.Path, "sample-repo-20261123T100000Z-0123abcd.report.json");
        var markdown = await File.ReadAllTextAsync(markdownPath);
        Assert.StartsWith(markdown, output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Markdown: {markdownPath}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"JSON: {jsonPath}", output.ToString(), StringComparison.Ordinal);
        foreach (var metric in new[]
        {
            "Retrieval recall at 10", "Citation validity", "Abstention precision", "Abstention recall", "Cost: total",
            "Cost per question: median", "Cost per question: 95th percentile", "Time per question: median", "Time per question: 95th percentile",
        })
        {
            Assert.Contains($"| {metric} | ", markdown, StringComparison.Ordinal);
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal(1.0, json.RootElement.GetProperty("metrics")[0].GetProperty("abstention").GetProperty("recall").GetDouble());
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task The_compare_command_writes_into_the_output_directory_under_a_name_the_files_cannot_choose()
    {
        using var directory = new ScratchDirectory();
        var baselineFile = await EvalFiles.Run(EvalFiles.Question("q", hits: HitOnLabel, evidence: EvidenceOnLabel)).WriteAsync(directory.Path);
        var hostile = EvalFiles.Run("../../../escape", EvalFiles.Question("q", hits: HitElsewhere, evidence: EvidenceOnLabel));
        var candidateFile = Path.Combine(directory.Path, "candidate.json");
        await File.WriteAllTextAsync(candidateFile, hostile.ToJson());
        var outputDirectory = Path.Combine(directory.Path, "out", "nested");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await EvalCommand.RunAsync(
            ["compare", baselineFile, candidateFile, "--output", outputDirectory], output, error, MustNotBuildServices);

        // A regression is a finding: the command still succeeds.
        Assert.Equal((EvalCommand.ExitOk, string.Empty), (exitCode, error.ToString()));
        Assert.Equal(
            ["compare-0123abcd-escape.json", "compare-0123abcd-escape.md"],
            Directory.GetFiles(outputDirectory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
        Assert.Contains("1 regressed, 0 improved", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("| `q` | `dev` | **regressed** |", output.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "compare-0123abcd-escape.json")));
        Assert.Equal(1, json.RootElement.GetProperty("counts").GetProperty("regressed").GetInt32());
    }

    [Fact]
    public async Task Wrong_arguments_and_unusable_files_end_with_exit_code_1_and_write_nothing()
    {
        using var directory = new ScratchDirectory();
        var good = await EvalFiles.Run(EvalFiles.Question("q")).WriteAsync(directory.Path);
        var notJson = Path.Combine(directory.Path, "broken.json");
        await File.WriteAllTextAsync(notJson, "{ not json");
        var future = Path.Combine(directory.Path, "future.json");
        await File.WriteAllTextAsync(future, (EvalFiles.Run(EvalFiles.Question("q")) with { SchemaVersion = 99 }).ToJson());

        await AssertFailsAsync(["report"], "'report' needs exactly one result file.", usage: true);
        await AssertFailsAsync(["report", good, good], "'report' needs exactly one result file.", usage: true);
        await AssertFailsAsync(["compare", good], "'compare' needs exactly two result files", usage: true);
        await AssertFailsAsync(["report", good, "--verbose"], "Unknown argument '--verbose'.", usage: true);
        await AssertFailsAsync(["report", good, "--output"], "'--output' needs a value.", usage: true);
        await AssertFailsAsync(["report", Path.Combine(directory.Path, "missing.json")], "Could not read the result file", usage: false);
        await AssertFailsAsync(["report", notJson], "is not a usable result file", usage: false);
        await AssertFailsAsync(["compare", good, future], "schemaVersion is 99", usage: false);
        await AssertFailsAsync(["metrics", good], "Expected a command", usage: true);

        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);

        static async Task AssertFailsAsync(string[] args, string expectedError, bool usage)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(EvalCommand.ExitError, await EvalCommand.RunAsync(args, output, error, MustNotBuildServices));
            Assert.Contains(expectedError, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(usage, error.ToString().Contains("Usage:", StringComparison.Ordinal));
            Assert.Equal(string.Empty, output.ToString());
        }
    }

    [Fact]
    public void Report_and_compare_arguments_are_parsed()
    {
        var report = EvalReportCommand.ParseArguments(["report", "--output", "out", "a.json"]);
        Assert.Equal(("report", "out"), (report.Command, report.OutputDirectory));
        Assert.Equal(["a.json"], report.ResultFiles);
        var compare = EvalReportCommand.ParseArguments(["compare", "a.json", "b.json"]);
        Assert.Equal(("compare", null), (compare.Command, compare.OutputDirectory));
        Assert.Equal(["a.json", "b.json"], compare.ResultFiles);
        Assert.Throws<EvalUsageException>(() => EvalReportCommand.ParseArguments(["run", "a.json"]));
        Assert.Throws<EvalUsageException>(() => EvalReportCommand.ParseArguments([]));
    }

    /// <summary>Reading result files needs no database, provider or settings: the host is never built.</summary>
    private static void MustNotBuildServices(Microsoft.Extensions.Hosting.HostApplicationBuilder builder) =>
        Assert.Fail("report and compare must not build the pipeline's services.");

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"trace-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
