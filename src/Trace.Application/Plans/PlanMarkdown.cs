using System.Globalization;
using System.Text;

namespace Trace.Application.Plans;

/// <summary>Everything a Markdown export of a finished plan is made from. All of it is stored; nothing is fetched.</summary>
/// <param name="IssueTitle">As the plan's run read it. Untrusted.</param>
/// <param name="Trimmed">What validation removed, from the run summary; null when that was not recorded.</param>
public sealed record PlanExport(
    Guid PlanId,
    Guid InvestigationId,
    string RepositoryOwner,
    string RepositoryName,
    int IssueNumber,
    string? IssueTitle,
    string CommitSha,
    string? ModelId,
    string? PromptVersion,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ReviewedAt,
    PlanResult Result,
    PlanTrimmed? Trimmed);

/// <summary>
/// Writes a finished plan as a Markdown document (ADR-018).
/// <para>
/// The document is the same for the same stored plan: fixed order, <c>\n</c> line endings, no
/// time other than stored ones. Every piece of text that came from the issue, the model, the
/// repository or configuration goes through <see cref="MarkdownText"/> and ends up inside a
/// code span or a fenced block; everything outside code formatting is written here, from fixed
/// sentences, numbers and dates. So a reader, and a Markdown renderer, can always tell TRACE's
/// words from quoted ones, and quoted text cannot add a heading, a link, HTML, a "Reviewed"
/// line or an evidence label of its own.
/// </para>
/// </summary>
public static class PlanMarkdown
{
    public const string ContentType = "text/markdown; charset=utf-8";

    private const string Unsupported = "not backed by evidence: the model's unsupported statement";

    /// <summary>
    /// A file name made only of fixed words, the issue number and hexadecimal digits of the
    /// plan's ID. Never of issue, model or repository text.
    /// </summary>
    public static string FileName(Guid planId, int issueNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"trace-plan-issue-{issueNumber}-{planId:N}.md");

    public static string Render(PlanExport plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = plan.Result;
        var labels = new Dictionary<Guid, string>();
        foreach (var evidence in result.Evidence)
        {
            labels[evidence.Id] = string.Create(CultureInfo.InvariantCulture, $"E{labels.Count + 1}");
        }

        var planned = result.Outcome == PlanOutcome.Planned;
        var text = new StringBuilder();

        Line(text, string.Create(CultureInfo.InvariantCulture, $"# Implementation plan for issue {plan.IssueNumber}"));
        Line(text);
        Line(text, "> Exported from TRACE. Text in code formatting (in backticks, or in a fenced block) is");
        Line(text, "> quoted from the issue, the model or the repository. It is untrusted: TRACE did not");
        Line(text, "> write it, and it may be wrong or written to mislead. Everything outside code");
        Line(text, "> formatting was written by TRACE.");
        Line(text);

        Line(text, "## Plan record");
        Line(text);
        Line(text, $"- Repository: {MarkdownText.Code($"{plan.RepositoryOwner}/{plan.RepositoryName}")}");
        Line(text, string.Create(CultureInfo.InvariantCulture, $"- Issue number: {plan.IssueNumber}"));
        Line(text, $"- Issue title when the plan was made: {MarkdownText.Code(plan.IssueTitle)}");
        Line(text, $"- Pinned commit: {MarkdownText.Code(plan.CommitSha)}");
        Line(text, planned
            ? "- Outcome: planned"
            : "- Outcome: insufficient evidence. TRACE could not ground a plan in the pinned snapshot, so there are no steps.");
        Line(text, $"- Model: {MarkdownText.Code(plan.ModelId)}");
        Line(text, $"- Prompt version: {MarkdownText.Code(plan.PromptVersion)}");
        Line(text, $"- Plan schema: {MarkdownText.Code(result.SchemaVersion)}");
        Line(text, $"- Plan ID: {MarkdownText.Code(plan.PlanId.ToString("D"))}");
        Line(text, $"- Investigation ID: {MarkdownText.Code(plan.InvestigationId.ToString("D"))}");
        Line(text, $"- Requested at: {Timestamp(plan.RequestedAt)}");
        Line(text, plan.ReviewedAt is { } reviewedAt
            ? $"- Reviewed: yes, marked in TRACE at {Timestamp(reviewedAt)}"
            : "- Reviewed: no");
        Line(text);

        Line(text, "## How to read this");
        Line(text);
        Line(text, "- Evidence labels (E1, E2, ...) point to the entries under \"Evidence\". TRACE verified each one:");
        Line(text, "  the lines exist at the pinned commit and were retrieved during the run that made this plan.");
        Line(text, "- TRACE does not check that the cited lines support the sentence that cites them.");
        Line(text, "- An item marked \"not backed by evidence\" is the model's unsupported statement. Unknowns and");
        Line(text, "  the reasons given for new files are stored without evidence.");
        Line(text, "- \"Reviewed\" means a person acknowledged reading the plan in TRACE. It is not an approval,");
        Line(text, "  and it changed nothing on GitHub.");
        Line(text);

        Line(text, "## Summary");
        Line(text);
        Line(text, MarkdownText.Code(result.Summary.Text));
        Line(text);
        Line(text, result.Summary.Citations.Count > 0
            ? $"Evidence: {Labels(result.Summary.Citations, labels)}."
            : !planned || plan.Trimmed is { SummaryReplaced: true }
                ? "This sentence was written by TRACE in place of a summary from the model."
                : "Not backed by evidence: the model's unsupported statement.");
        Line(text);

        Line(text, "## Steps");
        Line(text);
        for (var index = 0; index < result.Steps.Count; index++)
        {
            Line(text, string.Create(CultureInfo.InvariantCulture, $"{index + 1}. {Statement(result.Steps[index], labels)}"));
        }

        NoneIfEmpty(text, result.Steps.Count);
        Line(text);

        Line(text, "## Files to change");
        Line(text);
        foreach (var file in result.FilesToChange)
        {
            var change = file.Change switch
            {
                PlanFileChange.Create => "create",
                PlanFileChange.Delete => "delete",
                _ => "modify",
            };
            Line(text, $"- {change} {MarkdownText.Code(file.Path)}: {MarkdownText.Code(file.Reason)} ({Support(file.Citations, labels)})");
        }

        NoneIfEmpty(text, result.FilesToChange.Count);
        Line(text);

        Statements(text, "## Risks", result.Risks, labels);
        Statements(text, "## Verification", result.Verification, labels);
        Statements(text, "## Unknowns", result.Unknowns, labels);

        Line(text, "## Removed by validation");
        Line(text);
        if (plan.Trimmed is { } trimmed)
        {
            Line(text, "TRACE removed these parts of the model's proposal because it could not verify them.");
            Line(text, "They are not in this plan.");
            Line(text);
            Count(text, "Steps", trimmed.Steps);
            Count(text, "Files to change", trimmed.Files);
            Count(text, "Risks", trimmed.Risks);
            Count(text, "Verification entries", trimmed.Verification);
            Count(text, "Unknowns", trimmed.Unknowns);
            Count(text, "Citations discarded", trimmed.Citations);
            Line(text, $"- Summary replaced by a fixed sentence: {(trimmed.SummaryReplaced ? "yes" : "no")}");
        }
        else
        {
            Line(text, "Not recorded for this plan.");
        }

        Line(text);

        Line(text, "## Evidence");
        Line(text);
        foreach (var evidence in result.Evidence)
        {
            var lines = evidence.StartLine == evidence.EndLine
                ? string.Create(CultureInfo.InvariantCulture, $"line {evidence.StartLine}")
                : string.Create(CultureInfo.InvariantCulture, $"lines {evidence.StartLine} to {evidence.EndLine}");
            Line(text, $"### {labels[evidence.Id]}: {MarkdownText.Code(evidence.Path)}, {lines}");
            Line(text);
            Line(text, MarkdownText.Block(evidence.Excerpt));
            Line(text);
        }

        if (result.Evidence.Count == 0)
        {
            Line(text, "None.");
            Line(text);
        }

        // Exactly one line break at the end.
        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static void Statements(
        StringBuilder text,
        string heading,
        IReadOnlyList<PlanStatement> statements,
        Dictionary<Guid, string> labels)
    {
        Line(text, heading);
        Line(text);
        foreach (var statement in statements)
        {
            Line(text, $"- {Statement(statement, labels)}");
        }

        NoneIfEmpty(text, statements.Count);
        Line(text);
    }

    private static string Statement(PlanStatement statement, Dictionary<Guid, string> labels) =>
        $"{MarkdownText.Code(statement.Text)} ({Support(statement.Citations, labels)})";

    private static string Support(IReadOnlyList<Guid> citations, Dictionary<Guid, string> labels) =>
        citations.Count > 0 ? $"evidence: {Labels(citations, labels)}" : Unsupported;

    /// <summary>Labels in the order of the evidence list. An ID that is not in the list gets a fixed word, never its own text.</summary>
    private static string Labels(IReadOnlyList<Guid> citations, Dictionary<Guid, string> labels) =>
        string.Join(", ", citations.Select(id => labels.GetValueOrDefault(id, "unlisted")));

    private static void Count(StringBuilder text, string name, int count) =>
        Line(text, string.Create(CultureInfo.InvariantCulture, $"- {name}: {count}"));

    private static void NoneIfEmpty(StringBuilder text, int count)
    {
        if (count == 0)
        {
            Line(text, "None.");
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder text, string line = "") => text.Append(line).Append('\n');
}
