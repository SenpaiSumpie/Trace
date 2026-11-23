using System.Text.Json;
using Trace.Evals;

namespace Trace.UnitTests;

/// <summary>
/// Every metric of docs/evaluation.md on result files built by hand, with the expected numbers
/// worked out by hand in the comments.
/// </summary>
public class EvalMetricsTests
{
    private static readonly EvalRange Labeled = new("src/A.cs", 10, 20);

    [Fact]
    public void Recall_at_10_is_the_share_of_labeled_locations_that_a_top_10_hit_overlaps()
    {
        var eleven = Enumerable.Range(1, 10).Select(n => ($"src/Other{n}.cs", 1, 40)).Append(("src/A.cs", 1, 40)).ToArray();
        var run = EvalFiles.Run(
            // Found by the second hit: same file, lines 15-30 share 15-20 with the label.
            EvalFiles.Question("found", hits: [("src/B.cs", 1, 5), ("src/A.cs", 15, 30)]),

            // Two labels. The A hit starts on the line after the label ends: not found.
            // The C hit ends on the label's first line: found. 1 of 2.
            EvalFiles.Question(
                "half",
                expected: [Labeled, new EvalRange("src/C.cs", 5, 9)],
                hits: [("src/A.cs", 21, 40), ("src/C.cs", 1, 5)]),

            // The only overlapping hit is the eleventh: outside the top 10.
            EvalFiles.Question("rank-11", hits: eleven),

            // Right lines, wrong file; and a path that differs only in case is another file.
            EvalFiles.Question("wrong-file", hits: [("src/Z.cs", 10, 20), ("SRC/a.cs", 10, 20)]),

            // Not measured: no retrieval recorded, and a retrieval that failed.
            EvalFiles.Question("no-retrieval"),
            EvalFiles.Question("retrieval-error", retrievalError: "invalid_argument"),

            // An unanswerable question has no labeled location, so it is not part of recall.
            EvalFiles.Question("unanswerable", answerable: false, outcome: EvalOutcomes.InsufficientEvidence, hits: [("src/A.cs", 1, 40)]));

        var recall = EvalMetrics.Report(run).Metrics[0].RecallAt10;

        // Found 1 + 1 + 0 + 0 = 2 of 1 + 2 + 1 + 1 = 5 labeled locations.
        Assert.Equal(new RecallMetric(0.4, Found: 2, Labeled: 5, QuestionsMeasured: 4, QuestionsNotMeasured: 2, QuestionsAllFound: 1), recall);
        Assert.Equal(
            [(1, 1), (1, 2), (0, 1), (0, 1), (null, null), (null, null), (null, null)],
            EvalMetrics.Report(run).Questions.Select(question => (question.RecallFound, question.RecallLabeled)));
    }

    [Fact]
    public void Recall_uses_the_rank_a_hit_was_given_not_its_position_in_the_file()
    {
        var question = EvalFiles.Question("q") with
        {
            // Written out of order: the overlapping hit is rank 11, the others ranks 1 to 10.
            Retrieval = new EvalRetrieval(
                20,
                [
                    new EvalRetrievedLocation(11, "src/A.cs", 1, 40, null, null),
                    .. Enumerable.Range(1, 10).Select(rank => new EvalRetrievedLocation(rank, $"src/Other{rank}.cs", 1, 40, null, null)),
                ],
                ErrorCode: null),
        };

        Assert.Equal(0, EvalMetrics.Score(question).RecallFound);
    }

    [Fact]
    public void Citation_validity_is_validated_citations_over_everything_the_model_cited()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("two-valid-one-discarded", evidence: [("src/A.cs", 12, 14), ("src/B.cs", 1, 2)], rejectedCitations: 1),
            EvalFiles.Question("answer-discarded", outcome: EvalOutcomes.InsufficientEvidence, rejectedCitations: 2),
            EvalFiles.Question("failed", outcome: EvalOutcomes.Failed, errorCode: "model_error"),
            EvalFiles.Question("cited-nothing", outcome: EvalOutcomes.InsufficientEvidence));

        // 2 valid of 2 + 1 + 2 = 5 cited.
        Assert.Equal(new CitationMetric(0.4, Valid: 2, Cited: 5), EvalMetrics.Report(run).Metrics[0].CitationValidity);

        // Nothing cited is "nothing to measure", not 0% and not 100%.
        var none = EvalFiles.Run(EvalFiles.Question("cited-nothing", outcome: EvalOutcomes.InsufficientEvidence));
        Assert.Equal(new CitationMetric(null, 0, 0), EvalMetrics.Report(none).Metrics[0].CitationValidity);
    }

    [Fact]
    public void Abstention_precision_and_recall_count_insufficient_evidence_on_unanswerable_questions_as_correct()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("u-abstained-1", answerable: false, outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("u-abstained-2", answerable: false, outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("u-answered", answerable: false, outcome: EvalOutcomes.Answered, evidence: [("src/A.cs", 1, 2)]),
            EvalFiles.Question("u-failed", answerable: false, outcome: EvalOutcomes.Failed, errorCode: "model_error"),
            EvalFiles.Question("u-cancelled", answerable: false, outcome: EvalOutcomes.Cancelled),
            EvalFiles.Question("u-not-run", answerable: false, outcome: EvalOutcomes.NotRun),
            EvalFiles.Question("a-abstained", outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("a-answered", evidence: [("src/A.cs", 12, 14)]));

        // Correct abstentions: 2. Abstentions in all: 3 (one on an answerable question).
        // Unanswerable questions that ran to an end: 4 (two abstained, one answered, one failed);
        // the cancelled one and the one never run are left out.
        Assert.Equal(
            new AbstentionMetric(Precision: 2.0 / 3, Recall: 0.5, Correct: 2, Abstained: 3, Unanswerable: 4),
            EvalMetrics.Report(run).Metrics[0].Abstention);
    }

    [Fact]
    public void Abstention_is_not_applicable_without_abstentions_or_unanswerable_questions()
    {
        var run = EvalFiles.Run(EvalFiles.Question("a-answered", evidence: [("src/A.cs", 12, 14)]));

        Assert.Equal(new AbstentionMetric(null, null, 0, 0, 0), EvalMetrics.Report(run).Metrics[0].Abstention);
    }

    [Fact]
    public void Answers_citing_a_labeled_location_counts_answerable_questions_whose_evidence_overlaps_a_label()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("cites-label", evidence: [("src/B.cs", 1, 2), ("src/A.cs", 20, 22)]),
            EvalFiles.Question("cites-elsewhere", evidence: [("src/A.cs", 21, 30)]),
            EvalFiles.Question("abstained", outcome: EvalOutcomes.InsufficientEvidence),
            EvalFiles.Question("failed", outcome: EvalOutcomes.Failed, errorCode: "timed_out"),
            EvalFiles.Question("cancelled", outcome: EvalOutcomes.Cancelled),
            EvalFiles.Question("unanswerable", answerable: false, outcome: EvalOutcomes.Answered, evidence: [("src/A.cs", 10, 20)]));

        // 1 of the 4 answerable questions that ran to an end.
        Assert.Equal(new LabeledEvidenceMetric(0.25, Cited: 1, Questions: 4), EvalMetrics.Report(run).Metrics[0].LabeledEvidenceCited);
        Assert.Equal(
            [true, false, false, false, null, null],
            EvalMetrics.Report(run).Questions.Select(question => question.CitesLabeledEvidence));
    }

    [Fact]
    public void Cost_and_duration_are_the_median_and_95th_percentile_of_questions_that_ran_to_an_end()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("a", cost: 0.40m, durationMs: 400),
            EvalFiles.Question("b", cost: 0.10m, durationMs: 100),
            EvalFiles.Question("c", outcome: EvalOutcomes.InsufficientEvidence, cost: 0.20m, durationMs: 300),
            EvalFiles.Question("d", outcome: EvalOutcomes.Failed, errorCode: "timed_out", cost: 0.30m, durationMs: 200),

            // Left out: stopped from outside, or never started.
            EvalFiles.Question("cancelled", outcome: EvalOutcomes.Cancelled, cost: 9m, durationMs: 9_000),
            EvalFiles.Question("not-run", outcome: EvalOutcomes.NotRun, cost: null, durationMs: 0));

        var metrics = EvalMetrics.Report(run).Metrics[0];

        // Sorted costs 0.10 0.20 0.30 0.40: median (0.20 + 0.30) / 2; 95th percentile is the 4th of 4.
        Assert.Equal(new CostMetric(Questions: 4, TotalUsd: 1.00m, MedianUsd: 0.25m, P95Usd: 0.40m), metrics.Cost);
        Assert.Equal(new DurationMetric(Questions: 4, MedianMs: 250, P95Ms: 400), metrics.Duration);
    }

    [Fact]
    public void An_odd_number_of_values_has_its_middle_value_as_the_median_and_missing_costs_are_left_out()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("a", cost: null, durationMs: 30),
            EvalFiles.Question("b", cost: 0.5m, durationMs: 10),
            EvalFiles.Question("c", cost: null, durationMs: 20));

        var metrics = EvalMetrics.Report(run).Metrics[0];

        Assert.Equal(new CostMetric(1, 0.5m, 0.5m, 0.5m), metrics.Cost);
        Assert.Equal(new DurationMetric(3, MedianMs: 20, P95Ms: 30), metrics.Duration);
    }

    [Theory]
    [InlineData(1, 0)]      // the only value
    [InlineData(2, 1)]
    [InlineData(19, 18)]    // ceil(18.05) = 19th value
    [InlineData(20, 18)]    // exactly the 19th value: 19 of 20 values are at or below it
    [InlineData(21, 19)]    // ceil(19.95) = 20th value
    [InlineData(100, 94)]
    [InlineData(200, 189)]
    public void The_95th_percentile_is_the_nearest_rank(int count, int expectedIndex)
    {
        Assert.Equal(expectedIndex, EvalMetrics.NearestRank(count, 0.95));
    }

    [Fact]
    public void Every_metric_is_computed_overall_and_for_each_split_separately()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("dev-1", "dev", hits: [("src/A.cs", 1, 40)], evidence: [("src/A.cs", 12, 14)], cost: 0.1m, durationMs: 100),
            EvalFiles.Question("dev-2", "dev", hits: [("src/B.cs", 1, 40)], outcome: EvalOutcomes.InsufficientEvidence, rejectedCitations: 1, cost: 0.3m, durationMs: 300),
            EvalFiles.Question("test-1", "test", hits: [("src/A.cs", 1, 40)], evidence: [("src/B.cs", 1, 2)], cost: 0.5m, durationMs: 500),
            EvalFiles.Question("test-2", "test", answerable: false, outcome: EvalOutcomes.InsufficientEvidence, cost: 0.7m, durationMs: 700));

        var metrics = EvalMetrics.Report(run).Metrics;

        Assert.Equal(["all", "dev", "test"], metrics.Select(set => set.Scope));
        var (all, dev, test) = (metrics[0], metrics[1], metrics[2]);

        Assert.Equal((4, 2, 2), (all.Questions, dev.Questions, test.Questions));
        Assert.Equal(new OutcomeCounts(2, 2, 0, 0, 0), all.Outcomes);
        Assert.Equal(new OutcomeCounts(1, 1, 0, 0, 0), dev.Outcomes);
        Assert.Equal(new OutcomeCounts(1, 1, 0, 0, 0), test.Outcomes);

        Assert.Equal((2.0 / 3, 0.5, 1.0), (all.RecallAt10.Value, dev.RecallAt10.Value, test.RecallAt10.Value));
        Assert.Equal((2.0 / 3, 0.5, 1.0), (all.CitationValidity.Value, dev.CitationValidity.Value, test.CitationValidity.Value));

        // The one wrong abstention is in dev, the one correct abstention in test.
        Assert.Equal((0.5, 1.0), (all.Abstention.Precision, all.Abstention.Recall));
        Assert.Equal((0.0, (double?)null), (dev.Abstention.Precision, dev.Abstention.Recall));
        Assert.Equal((1.0, 1.0), (test.Abstention.Precision, test.Abstention.Recall));

        Assert.Equal((1.0 / 3, 0.5, 0.0), (all.LabeledEvidenceCited.Value, dev.LabeledEvidenceCited.Value, test.LabeledEvidenceCited.Value));
        Assert.Equal((1.6m, 0.4m, 1.2m), (all.Cost.TotalUsd, dev.Cost.TotalUsd, test.Cost.TotalUsd));
        Assert.Equal((0.4m, 0.2m, 0.6m), (all.Cost.MedianUsd, dev.Cost.MedianUsd, test.Cost.MedianUsd));
        Assert.Equal((0.7m, 0.3m, 0.7m), (all.Cost.P95Usd, dev.Cost.P95Usd, test.Cost.P95Usd));
        Assert.Equal((400.0, 200.0, 600.0), (all.Duration.MedianMs, dev.Duration.MedianMs, test.Duration.MedianMs));
        Assert.Equal((700.0, 300.0, 700.0), (all.Duration.P95Ms, dev.Duration.P95Ms, test.Duration.P95Ms));
    }

    [Fact]
    public void An_empty_run_has_no_value_for_any_metric_and_still_renders()
    {
        var report = EvalMetrics.Report(EvalFiles.Run());

        Assert.Equal(3, report.Metrics.Count);
        Assert.Empty(report.Questions);
        Assert.All(report.Metrics, set =>
        {
            Assert.Equal(0, set.Questions);
            Assert.Equal(new OutcomeCounts(0, 0, 0, 0, 0), set.Outcomes);
            Assert.Equal(new RecallMetric(null, 0, 0, 0, 0, 0), set.RecallAt10);
            Assert.Equal(new CitationMetric(null, 0, 0), set.CitationValidity);
            Assert.Equal(new AbstentionMetric(null, null, 0, 0, 0), set.Abstention);
            Assert.Equal(new LabeledEvidenceMetric(null, 0, 0), set.LabeledEvidenceCited);
            Assert.Equal(new CostMetric(0, null, null, null), set.Cost);
            Assert.Equal(new DurationMetric(0, null, null), set.Duration);
        });

        var markdown = EvalMarkdown.Report(report);
        Assert.Contains("| Retrieval recall at 10 | n/a (0 of 0 labeled locations) | n/a (0 of 0 labeled locations) | n/a (0 of 0 labeled locations) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Cost per question: median | n/a | n/a | n/a |", markdown, StringComparison.Ordinal);
        Assert.Contains("The result file has no questions.", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_in_which_every_question_failed_scores_zero_where_something_was_owed_and_nothing_elsewhere()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("dev-1", "dev", outcome: EvalOutcomes.Failed, errorCode: "model_error", hits: [("src/A.cs", 1, 40)], cost: 0m, durationMs: 40),
            EvalFiles.Question("dev-2", "dev", outcome: EvalOutcomes.Failed, errorCode: "budget_exhausted", hits: [], cost: 0m, durationMs: 20),
            EvalFiles.Question("test-1", "test", answerable: false, outcome: EvalOutcomes.Failed, errorCode: "model_error", cost: 0m, durationMs: 60),

            // The pipeline refused the request, so nothing was retrieved either.
            EvalFiles.Question("test-2", "test", outcome: EvalOutcomes.Failed, errorCode: "request_rejected", cost: 0m, durationMs: 1));

        var all = EvalMetrics.Report(run).Metrics[0];

        Assert.Equal(new OutcomeCounts(Answered: 0, InsufficientEvidence: 0, Failed: 4, Cancelled: 0, NotRun: 0), all.Outcomes);

        // Retrieval does not depend on the model: it is still measured where it was recorded.
        Assert.Equal(new RecallMetric(0.5, Found: 1, Labeled: 2, QuestionsMeasured: 2, QuestionsNotMeasured: 1, QuestionsAllFound: 1), all.RecallAt10);

        // Nothing was cited and nothing was abstained on: not applicable.
        Assert.Equal(new CitationMetric(null, 0, 0), all.CitationValidity);

        // The unanswerable question was owed an abstention and did not get one: recall 0 of 1.
        Assert.Equal(new AbstentionMetric(Precision: null, Recall: 0, Correct: 0, Abstained: 0, Unanswerable: 1), all.Abstention);

        // No answerable question rested on a labeled location: 0 of 3.
        Assert.Equal(new LabeledEvidenceMetric(0, Cited: 0, Questions: 3), all.LabeledEvidenceCited);

        // A failure still took time and (here) cost nothing.
        Assert.Equal(new CostMetric(4, 0m, 0m, 0m), all.Cost);
        Assert.Equal(new DurationMetric(4, MedianMs: 30, P95Ms: 60), all.Duration);
    }

    [Fact]
    public void A_run_that_was_stopped_before_any_question_has_only_outcome_counts()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("a", outcome: EvalOutcomes.NotRun, cost: null, durationMs: 0),
            EvalFiles.Question("b", answerable: false, outcome: EvalOutcomes.NotRun, cost: null, durationMs: 0)) with
        {
            Status = EvalRunStatus.Cancelled,
        };

        var report = EvalMetrics.Report(run);
        var all = report.Metrics[0];

        Assert.Equal(new OutcomeCounts(0, 0, 0, 0, NotRun: 2), all.Outcomes);
        Assert.Equal(new RecallMetric(null, 0, 0, 0, QuestionsNotMeasured: 1, 0), all.RecallAt10);
        Assert.Equal(new AbstentionMetric(null, null, 0, 0, 0), all.Abstention);
        Assert.Equal(new LabeledEvidenceMetric(null, 0, 0), all.LabeledEvidenceCited);
        Assert.Equal(new CostMetric(0, null, null, null), all.Cost);
        Assert.Equal(new DurationMetric(0, null, null), all.Duration);
        Assert.Contains(report.Notes, note => note.Contains("stopped early", StringComparison.Ordinal));
    }

    [Fact]
    public void Notes_say_what_limits_the_numbers()
    {
        var plain = EvalFiles.Run(EvalFiles.Question("a")) with
        {
            Chat = new EvalChatInfo("anthropic", "some-model", "question-v2"),
            Snapshot = new EvalSnapshotInfo(new string('b', 40), "main", "lines-v1", "voyage", "some-embedding"),
        };
        Assert.Empty(EvalMetrics.Report(plain).Notes);

        var limited = EvalFiles.Run(EvalFiles.Question("a")) with
        {
            SchemaVersion = 1,
            Prices = null,
            Dataset = new EvalDatasetInfo("d", "1", new string('c', 40), RevisionMatchesSnapshot: false, "dev", 1),
        };
        var notes = string.Join('\n', EvalMetrics.Report(limited).Notes);

        Assert.Contains("chat model is the deterministic fake", notes, StringComparison.Ordinal);
        Assert.Contains("embeddings are the deterministic fake", notes, StringComparison.Ordinal);
        Assert.Contains("schema version 1", notes, StringComparison.Ordinal);
        Assert.Contains("limited to one split", notes, StringComparison.Ordinal);
        Assert.Contains("not the revision the dataset was labeled against", notes, StringComparison.Ordinal);
        Assert.Contains("cost is not estimated", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_prints_every_metric_for_all_and_both_splits_and_never_a_question_an_answer_or_source_text()
    {
        var run = EvalFiles.Run(
            EvalFiles.Question("dev-1", "dev", hits: [("src/A.cs", 1, 40)], evidence: [("src/A.cs", 12, 14)], rejectedCitations: 1, cost: 0.1234m, durationMs: 1_500),
            EvalFiles.Question("test-1", "test", answerable: false, outcome: EvalOutcomes.InsufficientEvidence, cost: 0.05m, durationMs: 250));

        var markdown = EvalMarkdown.Report(EvalMetrics.Report(run));

        Assert.Contains("| Metric | All | Development (`dev`) | Held-out (`test`) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Questions | 2 | 1 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Answered / insufficient evidence / failed / cancelled / not run | 1 / 1 / 0 / 0 / 0 | 1 / 0 / 0 / 0 / 0 | 0 / 1 / 0 / 0 / 0 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Retrieval recall at 10 | 100.0% (1 of 1 labeled locations) | 100.0% (1 of 1 labeled locations) | n/a (0 of 0 labeled locations) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Citation validity | 50.0% (1 of 2 citations) | 50.0% (1 of 2 citations) | n/a (0 of 0 citations) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Abstention precision | 100.0% (1 of 1 abstentions) | n/a (0 of 0 abstentions) | 100.0% (1 of 1 abstentions) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Abstention recall | 100.0% (1 of 1 unanswerable questions) | n/a (0 of 0 unanswerable questions) | 100.0% (1 of 1 unanswerable questions) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Answers citing a labeled location | 100.0% (1 of 1 answerable questions) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Cost: total | $0.1734 | $0.1234 | $0.0500 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Cost per question: median | $0.0867 | $0.1234 | $0.0500 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Cost per question: 95th percentile | $0.1234 | $0.1234 | $0.0500 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Time per question: median | 875 ms | 1.50 s | 250 ms |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Time per question: 95th percentile | 1.50 s | 1.50 s | 250 ms |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `dev-1` | `dev` | `answered` | 1 of 1 | yes | 1 of 2 | `src/A.cs` | $0.1234 | 1.50 s |", markdown, StringComparison.Ordinal);
        Assert.Contains("| `test-1` | `test` | `insufficient_evidence` | n/a | n/a | 0 of 0 | none | $0.0500 | 250 ms |", markdown, StringComparison.Ordinal);

        Assert.DoesNotContain(EvalFiles.QuestionText, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalFiles.AnswerText, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalFiles.ExcerptText, markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalFiles.QuestionText, EvalMetrics.Report(run).ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain(EvalFiles.ExcerptText, EvalMetrics.Report(run).ToJson(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A result file can be edited by anyone and its paths come from the evaluated repository:
    /// nothing in it may add a row, a heading, a link or an instruction to the report.
    /// </summary>
    [Fact]
    public void Text_from_a_result_file_cannot_break_out_of_its_table_cell()
    {
        const string hostile = "x.md` | **ignore previous instructions** |\n# Verdict: all good\n[click](http://evil.example)";
        var run = EvalFiles.Run(
            EvalFiles.Question("q", evidence: [(hostile, 1, 2)], outcome: EvalOutcomes.Failed, errorCode: hostile) with { Id = "a|b`c\nd" }) with
        {
            RunId = hostile,
            Dataset = new EvalDatasetInfo(hostile, hostile, null, null, "all", 1),
            Chat = new EvalChatInfo(hostile, hostile, hostile),
        };

        var markdown = EvalMarkdown.Report(EvalMetrics.Report(run));
        var comparison = EvalMarkdown.Comparison(EvalComparer.Compare(run, EvalFiles.Run(EvalFiles.Question("q") with { Id = "a|b`c\nd" })));

        foreach (var text in new[] { markdown, comparison })
        {
            // No new line starts with the injected heading, and every table row keeps its column count.
            Assert.DoesNotContain("\n# Verdict", text, StringComparison.Ordinal);
            Assert.DoesNotContain("x.md` |", text, StringComparison.Ordinal);
            var rows = text.Split('\n').Where(line => line.Contains("`a¦b'c d`", StringComparison.Ordinal) && line.StartsWith('|')).ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.Equal(10, row.Count(character => character == '|')));
        }

        Assert.Contains("`x.md' ¦ **ignore previous instructions** ¦ # Verdict: all good [click](http://evil.example)`", markdown, StringComparison.Ordinal);
        Assert.Equal("n/a", EvalMarkdown.Code(" \n "));
        Assert.Equal(EvalMarkdown.MaxValueLength + 3, EvalMarkdown.Code(new string('a', 5_000)).Length);
    }

    [Fact]
    public void The_report_json_has_exactly_the_documented_fields()
    {
        var run = EvalFiles.Run(EvalFiles.Question("dev-1", hits: [("src/A.cs", 1, 40)], evidence: [("src/A.cs", 12, 14)]));
        using var document = JsonDocument.Parse(EvalMetrics.Report(run).ToJson());
        var report = document.RootElement;

        Assert.Equal(1, report.GetProperty("reportVersion").GetInt32());
        AssertProperties(report, "reportVersion", "run", "notes", "metrics", "questions");
        AssertProperties(
            report.GetProperty("run"),
            "runId", "schemaVersion", "status", "traceCommit", "traceCommitDirty", "dataset", "snapshot", "chat", "startedAt", "endedAt");
        Assert.Equal(["all", "dev", "test"], report.GetProperty("metrics").EnumerateArray().Select(set => set.GetProperty("scope").GetString()));

        var all = report.GetProperty("metrics")[0];
        AssertProperties(
            all, "scope", "questions", "outcomes", "recallAt10", "citationValidity", "abstention", "labeledEvidenceCited", "cost", "duration");
        AssertProperties(all.GetProperty("outcomes"), "answered", "insufficientEvidence", "failed", "cancelled", "notRun");
        AssertProperties(
            all.GetProperty("recallAt10"), "value", "found", "labeled", "questionsMeasured", "questionsNotMeasured", "questionsAllFound");
        AssertProperties(all.GetProperty("citationValidity"), "value", "valid", "cited");
        AssertProperties(all.GetProperty("abstention"), "precision", "recall", "correct", "abstained", "unanswerable");
        AssertProperties(all.GetProperty("labeledEvidenceCited"), "value", "cited", "questions");
        AssertProperties(all.GetProperty("cost"), "questions", "totalUsd", "medianUsd", "p95Usd");
        AssertProperties(all.GetProperty("duration"), "questions", "medianMs", "p95Ms");
        AssertProperties(
            report.GetProperty("questions")[0],
            "id", "split", "answerable", "outcome", "errorCode", "recallFound", "recallLabeled", "citesLabeledEvidence", "evidenceFiles",
            "validCitations", "rejectedCitations", "estimatedCostUsd", "durationMs", "ranToAnEnd", "hasExpectedOutcome");

        // A metric with nothing to measure is null in the JSON.
        Assert.Equal(JsonValueKind.Null, all.GetProperty("abstention").GetProperty("precision").ValueKind);
        Assert.Equal(1.0, all.GetProperty("recallAt10").GetProperty("value").GetDouble());
    }

    private static void AssertProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name));
}

/// <summary>Result files built by hand. Defaults describe an answerable development question that was answered.</summary>
internal static class EvalFiles
{
    public const string QuestionText = "Why does the zebracorn skip the first page?";
    public const string AnswerText = "Because the zebracorn offset is wrong.";
    public const string ExcerptText = "var zebracornOffset = page * size;";

    /// <param name="expected">Default: lines 10-20 of src/A.cs; none for an unanswerable question.</param>
    /// <param name="hits">The retrieval's hits in rank order; null with no <paramref name="retrievalError"/> records no retrieval.</param>
    public static EvalQuestionResult Question(
        string id,
        string split = "dev",
        bool answerable = true,
        string outcome = EvalOutcomes.Answered,
        string? errorCode = null,
        EvalRange[]? expected = null,
        (string Path, int StartLine, int EndLine)[]? hits = null,
        string? retrievalError = null,
        (string Path, int StartLine, int EndLine)[]? evidence = null,
        int rejectedCitations = 0,
        decimal? cost = 0m,
        long durationMs = 100) =>
        new(
            id,
            split,
            QuestionText,
            answerable,
            expected ?? (answerable ? [new EvalRange("src/A.cs", 10, 20)] : []),
            hits is null && retrievalError is null
                ? null
                : new EvalRetrieval(
                    10,
                    [.. (hits ?? []).Select((hit, index) => new EvalRetrievedLocation(index + 1, hit.Path, hit.StartLine, hit.EndLine, index + 1, null))],
                    retrievalError),
            outcome,
            errorCode,
            InvestigationId: null,
            outcome is EvalOutcomes.Answered or EvalOutcomes.InsufficientEvidence ? AnswerText : null,
            [.. (evidence ?? []).Select(item => new EvalEvidence(item.Path, item.StartLine, item.EndLine, "Shows it.", ExcerptText))],
            Unknowns: [],
            Limitations: [],
            ToolCalls: [],
            rejectedCitations,
            new EvalTokens(0, 0, 0, 0),
            cost,
            durationMs);

    public static EvalRunResult Run(params EvalQuestionResult[] questions) => Run("0123abcd0123abcd0123abcd0123abcd", questions);

    public static EvalRunResult Run(string runId, params EvalQuestionResult[] questions) =>
        new(
            EvalRunResult.CurrentSchemaVersion,
            runId,
            EvalRunStatus.Completed,
            new string('a', 40),
            TraceCommitDirty: false,
            new EvalDatasetInfo("sample-repo", "1", new string('b', 40), RevisionMatchesSnapshot: true, "all", questions.Length),
            new EvalSnapshotInfo(new string('b', 40), "main", "lines-v1", "deterministic-fake", "fake-v1"),
            new EvalChatInfo("deterministic-fake", "fake-extractive-v1", "question-v2"),
            new EvalPriceInfo("USD", 10m, 50m, 1m, 12.5m),
            new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 23, 10, 0, 2, TimeSpan.Zero),
            new EvalTotals(questions.Length, 0, 0, 0, 0, 0, new EvalTokens(0, 0, 0, 0), 0m),
            questions);
}
