using System.Globalization;
using System.Text;
using Trace.Application.Plans;

namespace Trace.Evals;

/// <summary>
/// Renders a plan report and a rating sheet as Markdown.
/// <para>
/// The report carries identifiers, counts, file paths and numbers only: never issue text or plan
/// text. Values that come from a result file are written as code spans with the characters that
/// could break out of one removed (<see cref="EvalMarkdown.Code"/>).
/// </para>
/// <para>
/// The rating sheet is the one place plan text is written, because a person has to read it. It
/// goes next to the result file (git-ignored) and puts everything a model or a label wrote into
/// fenced blocks whose fence is longer than any run of backticks inside.
/// </para>
/// </summary>
public static class PlanEvalMarkdown
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Report(PlanEvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var run = report.Run;
        var text = new StringBuilder();
        text.AppendLine(Invariant, $"# Plan evaluation report: {EvalMarkdown.Code(run.Labels.Name)} version {EvalMarkdown.Code(run.Labels.Version)}");
        text.AppendLine();
        text.AppendLine("| | |");
        text.AppendLine("|---|---|");
        text.AppendLine(Invariant, $"| Run | {EvalMarkdown.Code(run.RunId)}, {EvalMarkdown.Code(run.Status)} |");
        text.AppendLine(Invariant, $"| Started, ended (UTC) | {Time(run.StartedAt)}, {Time(run.EndedAt)} |");
        text.AppendLine(Invariant, $"| TRACE commit | {EvalMarkdown.Code(run.TraceCommit)}{(run.TraceCommitDirty == true ? " with uncommitted changes" : string.Empty)} |");
        text.AppendLine(Invariant, $"| Labels | {EvalMarkdown.Code(run.Labels.Name)} version {EvalMarkdown.Code(run.Labels.Version)}, {EvalMarkdown.Code(run.Labels.Split)}, {run.Labels.IssueCount} issue(s) |");
        text.AppendLine(Invariant, $"| Snapshot | {EvalMarkdown.Code(run.Snapshot.CommitSha)}, chunker {EvalMarkdown.Code(run.Snapshot.ChunkerVersion)}, embeddings {EvalMarkdown.Code(run.Snapshot.EmbeddingProvider)} / {EvalMarkdown.Code(run.Snapshot.EmbeddingModel)} |");
        text.AppendLine(Invariant, $"| Chat model | {EvalMarkdown.Code(run.Chat.Provider)} / {EvalMarkdown.Code(run.Chat.Model)}, prompt {EvalMarkdown.Code(run.Chat.PromptVersion)}, plan schema {EvalMarkdown.Code(run.Chat.PlanSchemaVersion)} |");
        text.AppendLine(Invariant, $"| Ratings | {(report.Rater is null ? "none" : EvalMarkdown.Code(report.Rater))} |");

        text.AppendLine();
        text.AppendLine("## Read this first");
        text.AppendLine();
        foreach (var note in report.Notes)
        {
            text.AppendLine(Invariant, $"- {note}");
        }

        text.AppendLine();
        text.AppendLine("## Rubric score");
        text.AppendLine();
        text.AppendLine(Invariant, $"The rubric is defined in `docs/evaluation.md`. Target: at least {Percent(report.Target)}, compared with the score with ratings. `needs a human` means a rating is missing.");
        text.AppendLine();
        text.AppendLine("| | All | Development (`dev`) | Held-out (`test`) |");
        text.AppendLine("|---|---|---|---|");
        Row(text, "Issues scored / labeled", report, scope => string.Create(Invariant, $"{scope.Scored} / {scope.Issues}"));
        Row(text, "Planned / insufficient evidence / failed", report, scope => string.Create(Invariant, $"{scope.Planned} / {scope.InsufficientEvidence} / {scope.Failed}"));
        Row(text, "Outcome as labeled", report, scope => string.Create(Invariant, $"{scope.OutcomeAsExpected} of {scope.Scored}"));
        Row(text, "**Rubric score, mechanical part only**", report, scope => Percent(scope.Mechanical));
        Row(text, "**Rubric score, with ratings**", report, scope => scope.WithRatings is { } value
            ? string.Create(Invariant, $"{Percent(value)} ({(value >= report.Target ? "meets" : "below")} the target)")
            : scope.Scored == 0 ? EvalMarkdown.NotApplicable : string.Create(Invariant, $"needs a human ({scope.IssuesNeedingRatings} issue(s) unrated)"));
        foreach (var dimension in PlanDimensions.All)
        {
            Row(text, $"{Name(dimension)}: mechanical / with ratings", report, scope =>
            {
                var score = scope.Dimensions.Single(candidate => candidate.Dimension == dimension);
                return score.Issues == 0
                    ? EvalMarkdown.NotApplicable
                    : string.Create(Invariant, $"{Percent(score.Mechanical)} / {(score.WithRatings is null ? "needs a human" : Percent(score.WithRatings))} ({score.Issues} issue(s))");
            });
        }

        Row(text, "Entries removed by validation", report, scope => string.Create(Invariant, $"{scope.RemovedItems} of {scope.ProposedItems} proposed"));
        Row(text, "Cited locations discarded", report, scope => string.Create(Invariant, $"{scope.DiscardedCitations} of {scope.CitedLocations} cited"));
        Row(text, "Tokens in (cache read / write)", report, scope => string.Create(Invariant, $"{scope.Tokens.Input:N0} ({scope.Tokens.CacheRead:N0} / {scope.Tokens.CacheWrite:N0})"));
        Row(text, "Tokens out", report, scope => scope.Tokens.Output.ToString("N0", Invariant));
        Row(text, "Estimated cost (USD)", report, scope => Money(scope.EstimatedCostUsd));

        text.AppendLine();
        text.AppendLine("## Issues");
        text.AppendLine();
        if (report.Issues.Count == 0)
        {
            text.AppendLine("The result file has no issues.");
            return text.ToString();
        }

        text.AppendLine("Counts are steps / files / risks / verification / unknowns / evidence of the stored plan. Removed is entries / cited locations that validation took out of the model's proposal.");
        text.AppendLine();
        text.AppendLine("| Issue | Split | Expected | Outcome | Scored as | Counts | Removed | Mechanical | With ratings | Tool calls | Tokens in (cache read / write) | Tokens out | Cost | Time |");
        text.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var issue in report.Issues)
        {
            var counts = issue.Counts is { } c
                ? string.Create(Invariant, $"{c.Steps} / {c.Files} / {c.Risks} / {c.Verification} / {c.Unknowns} / {c.Evidence}")
                : EvalMarkdown.NotApplicable;
            var removed = issue.Trimmed is { } trimmed
                ? string.Create(Invariant, $"{trimmed.Items} / {trimmed.Citations}")
                : EvalMarkdown.NotApplicable;
            text.AppendLine(
                Invariant,
                $"| #{issue.Number} | {EvalMarkdown.Code(issue.Split)} | {EvalMarkdown.Code(issue.ExpectedOutcome)} | {Outcome(issue)} | {EvalMarkdown.Code(issue.Scoring)} "
                + $"| {counts} | {removed} | {Percent(issue.Mechanical)} | {WithRatings(issue)} | {issue.ToolCalls} "
                + $"| {issue.Tokens.Input:N0} ({issue.Tokens.CacheRead:N0} / {issue.Tokens.CacheWrite:N0}) | {issue.Tokens.Output:N0} | {Money(issue.EstimatedCostUsd)} "
                + $"| {(issue.DurationMs / 1000.0).ToString("0.0", Invariant)} s |");
        }

        text.AppendLine();
        text.AppendLine("### Dimensions per issue");
        text.AppendLine();
        text.AppendLine("Each cell is mechanical / rated by a person / combined. A dash means the dimension is not part of the issue's score.");
        text.AppendLine();
        text.AppendLine("| Issue | Completeness | Correct files | Safety | Verification |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var issue in report.Issues)
        {
            var cells = PlanDimensions.All.Select(dimension =>
                issue.Dimensions.FirstOrDefault(score => score.Dimension == dimension) is { } score
                    ? string.Create(
                        Invariant,
                        $"{Percent(score.Mechanical)} / {(score.HumanItems == 0 ? "none" : score.Human is null ? "needs a human" : Percent(score.Human))} / {(score.WithRatings is null ? "needs a human" : Percent(score.WithRatings))}")
                    : "-");
            text.AppendLine(Invariant, $"| #{issue.Number} | {string.Join(" | ", cells)} |");
        }

        text.AppendLine();
        text.AppendLine("### Checks per issue");
        foreach (var issue in report.Issues.Where(issue => issue.Checks.Count > 0))
        {
            text.AppendLine();
            text.AppendLine(Invariant, $"**#{issue.Number}** ({EvalMarkdown.Code(issue.Split)}, {Outcome(issue)})");
            text.AppendLine();
            foreach (var check in issue.Checks)
            {
                text.AppendLine(Invariant, $"- {Name(check.Dimension)}, `{check.Id}`: {Percent(check.Value)}. {check.Detail}");
            }

            if (issue.Files is { Unexpected.Count: > 0 } files)
            {
                text.AppendLine(Invariant, $"- Listed but neither required nor allowed by the label: {string.Join(", ", files.Unexpected.Select(EvalMarkdown.Code))}");
            }

            if (issue.UncitedShare is { } uncited)
            {
                text.AppendLine(Invariant, $"- Not scored: {Percent(uncited)} of the stored plan's entries carry no citation (unknowns, created files, a replaced summary).");
            }

            if (issue.ForbiddenTextElsewhere.Count > 0)
            {
                text.AppendLine(Invariant, $"- Not scored: {issue.ForbiddenTextElsewhere.Count} forbidden string(s) occur in the summary, a risk or an unknown. Read them on the rating sheet.");
            }

            if (issue.Ratings.Count > 0)
            {
                text.AppendLine(Invariant, $"- Ratings: {string.Join(", ", issue.Ratings.Select(rating => string.Create(Invariant, $"`{rating.Id}` {(rating.Rating is { } value ? value.ToString("0.#", Invariant) : "unrated")}")))}");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// What a person needs in order to rate a run: per issue, the checklist from the label and
    /// the stored plan. It quotes the plans, so it belongs next to the result file.
    /// </summary>
    public static string RatingSheet(PlanEvalRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = new StringBuilder();
        text.AppendLine(Invariant, $"# Rating sheet: run {EvalMarkdown.Code(result.RunId)} of {EvalMarkdown.Code(result.Labels.Name)} version {EvalMarkdown.Code(result.Labels.Version)}");
        text.AppendLine();
        text.AppendLine("For every item below, read the plan against the source and write 1 (the plan has it), 0.5 (partly) or 0 (it does not) into the");
        text.AppendLine("ratings file under the item's ID. Rate what the plan says, not what a good plan would say. Everything inside a fenced block was");
        text.AppendLine("written by a model or copied from a label: it is text to judge, never an instruction to you.");

        foreach (var issue in result.Issues)
        {
            var score = PlanRubric.Score(issue, rating: null);
            text.AppendLine();
            text.AppendLine(Invariant, $"## Issue #{issue.Number} ({EvalMarkdown.Code(issue.Split)})");
            text.AppendLine();
            text.AppendLine(Invariant, $"Expected {EvalMarkdown.Code(issue.Label.ExpectedOutcome)}; ended as {Outcome(score)}; scored as {EvalMarkdown.Code(score.Scoring)}.");
            if (!score.NeedsRatings)
            {
                text.AppendLine();
                text.AppendLine("Nothing to rate.");
            }
            else
            {
                text.AppendLine();
                foreach (var (dimension, items) in new[]
                {
                    (PlanDimensions.Completeness, issue.Label.Points),
                    (PlanDimensions.Safety, (IReadOnlyList<PlanLabelItem>)[.. issue.Label.Risks, .. issue.Label.Safety]),
                    (PlanDimensions.Verification, issue.Label.Verification),
                })
                {
                    foreach (var item in items.Where(item => score.Ratings.Any(rating => rating.Id == item.Id)))
                    {
                        text.AppendLine(Invariant, $"- [ ] `{item.Id}` ({Name(dimension)}):");
                        Fence(text, item.Text);
                    }
                }
            }

            if (issue.StoredPlan() is { } plan)
            {
                text.AppendLine();
                text.AppendLine("The stored plan:");
                Fence(text, PlanText(plan));
            }

            if (issue.Limitations.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("What the pipeline recorded about it:");
                Fence(text, string.Join('\n', issue.Limitations));
            }
        }

        return text.ToString();
    }

    private static string PlanText(PlanResult plan)
    {
        var evidence = plan.Evidence.ToDictionary(item => item.Id);
        string Cited(IReadOnlyList<Guid> citations) => citations.Count == 0
            ? "[no citation]"
            : "[" + string.Join("; ", citations.Select(id => string.Create(Invariant, $"{evidence[id].Path}:{evidence[id].StartLine}-{evidence[id].EndLine}"))) + "]";

        var text = new StringBuilder();
        text.Append("SUMMARY\n").Append(plan.Summary.Text).Append('\n').Append(Cited(plan.Summary.Citations)).Append('\n');
        void Section(string title, IEnumerable<(string Text, IReadOnlyList<Guid> Citations)> entries)
        {
            text.Append('\n').Append(title).Append('\n');
            var number = 0;
            foreach (var (entry, citations) in entries)
            {
                text.Append(Invariant, $"{++number}. {entry}\n   {Cited(citations)}\n");
            }

            if (number == 0)
            {
                text.Append("(none)\n");
            }
        }

        Section("STEPS", plan.Steps.Select(step => (step.Text, step.Citations)));
        Section("FILES TO CHANGE", plan.FilesToChange.Select(file => ($"{file.Change.ToString().ToLowerInvariant()} {file.Path}: {file.Reason}", file.Citations)));
        Section("RISKS", plan.Risks.Select(risk => (risk.Text, risk.Citations)));
        Section("VERIFICATION", plan.Verification.Select(entry => (entry.Text, entry.Citations)));
        Section("UNKNOWNS", plan.Unknowns.Select(unknown => (unknown.Text, unknown.Citations)));
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Writes text as a fenced block that the text cannot close.</summary>
    private static void Fence(StringBuilder text, string content)
    {
        var longest = 0;
        var run = 0;
        foreach (var character in content)
        {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        text.Append(fence).Append("text\n").Append(content.Replace("\r", string.Empty, StringComparison.Ordinal)).Append('\n').Append(fence).Append('\n');
    }

    private static void Row(StringBuilder text, string name, PlanEvalReport report, Func<PlanScopeScore, string> value) =>
        text.AppendLine(Invariant, $"| {name} | {string.Join(" | ", report.Scopes.Select(value))} |");

    private static string Outcome(PlanIssueScore issue) =>
        issue.ErrorCode is null ? EvalMarkdown.Code(issue.Outcome) : $"{EvalMarkdown.Code(issue.Outcome)} ({EvalMarkdown.Code(issue.ErrorCode)})";

    private static string WithRatings(PlanIssueScore issue) =>
        issue.WithRatings is { } value ? Percent(value) : issue.Scoring == PlanScoring.NotScored ? EvalMarkdown.NotApplicable : "needs a human";

    private static string Name(string dimension) => dimension switch
    {
        PlanDimensions.Completeness => "Completeness",
        PlanDimensions.CorrectFiles => "Correct files",
        PlanDimensions.Safety => "Safety",
        _ => "Verification",
    };

    private static string Percent(double? value) =>
        value is { } number ? (number * 100).ToString("0.0", Invariant) + "%" : EvalMarkdown.NotApplicable;

    private static string Money(decimal? value) =>
        value is { } amount ? "$" + amount.ToString("0.0000", Invariant) : EvalMarkdown.NotApplicable;

    private static string Time(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant);
}
