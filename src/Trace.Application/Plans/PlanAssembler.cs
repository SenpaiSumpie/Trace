using System.Globalization;
using Trace.Application.Retrieval;
using Trace.Domain;

namespace Trace.Application.Plans;

/// <summary>What validation removed from a model's proposal, as counts. Zero everywhere means nothing was removed.</summary>
/// <param name="Citations">Different cited locations that were discarded.</param>
/// <param name="SummaryReplaced">The model's summary was not kept; a fixed sentence stands in its place.</param>
public sealed record PlanTrimmed(
    int Citations,
    int Steps,
    int Files,
    int Risks,
    int Verification,
    int Unknowns,
    bool SummaryReplaced)
{
    public static PlanTrimmed Nothing { get; } = new(0, 0, 0, 0, 0, 0, SummaryReplaced: false);

    /// <summary>Entries of the proposal that are not in the plan.</summary>
    public int Items => Steps + Files + Risks + Verification + Unknowns + (SummaryReplaced ? 1 : 0);

    public bool Any => Items > 0 || Citations > 0;
}

public sealed record AssembledStatement(string Text, IReadOnlyList<Citation> Citations);

public sealed record AssembledFile(string Path, PlanFileChange Change, string Reason, IReadOnlyList<Citation> Citations);

/// <summary>
/// What is left of a proposal after validation, still citing locations rather than stored
/// evidence rows.
/// </summary>
/// <param name="Limitations">Fixed sentences saying why the outcome is what it is and what was removed. Never model text.</param>
/// <param name="Removals">One fixed sentence per removed entry, for the investigation's event list. Never model text.</param>
public sealed record AssembledPlan(
    PlanOutcome Outcome,
    AssembledStatement Summary,
    IReadOnlyList<AssembledStatement> Steps,
    IReadOnlyList<AssembledFile> FilesToChange,
    IReadOnlyList<AssembledStatement> Risks,
    IReadOnlyList<AssembledStatement> Verification,
    IReadOnlyList<AssembledStatement> Unknowns,
    PlanTrimmed Trimmed,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Removals)
{
    /// <summary>Every location the kept entries cite, each once, in the order first cited.</summary>
    public IReadOnlyList<Citation> CitedLocations() =>
        [.. Summary.Citations
            .Concat(Steps.SelectMany(step => step.Citations))
            .Concat(FilesToChange.SelectMany(file => file.Citations))
            .Concat(Risks.SelectMany(risk => risk.Citations))
            .Concat(Verification.SelectMany(entry => entry.Citations))
            .Concat(Unknowns.SelectMany(unknown => unknown.Citations))
            .Distinct()];

    /// <summary>The stored document, once every cited location has its evidence row.</summary>
    public PlanResult ToResult(IReadOnlyDictionary<Citation, PlanEvidence> evidence)
    {
        PlanStatement Statement(AssembledStatement statement) =>
            new(statement.Text, [.. statement.Citations.Select(citation => evidence[citation].Id)]);

        return new PlanResult(
            PlanResult.CurrentSchemaVersion,
            Outcome,
            Statement(Summary),
            [.. Steps.Select(Statement)],
            [.. FilesToChange.Select(file => new PlanFile(
                file.Path, file.Change, file.Reason, [.. file.Citations.Select(citation => evidence[citation].Id)]))],
            [.. Risks.Select(Statement)],
            [.. Verification.Select(Statement)],
            [.. Unknowns.Select(Statement)],
            [.. CitedLocations().Select(citation => evidence[citation])]);
    }
}

/// <summary>Whether a path a model wrote may appear in a plan at all. Nothing is repaired: a path passes or it does not.</summary>
public static class PlanPaths
{
    /// <summary>
    /// A repository-relative path with forward slashes: not empty, at most
    /// <see cref="SourceChunk.MaxPathLength"/> characters, no backslash, no control character, no
    /// leading or trailing white space, no empty, <c>.</c> or <c>..</c> segment (so no leading
    /// slash and no way up), no drive letter and no home directory.
    /// </summary>
    public static bool IsClean(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.Length <= SourceChunk.MaxPathLength
        && path == path.Trim()
        && !path.Contains('\\')
        && !path.Any(char.IsControl)
        && !path.StartsWith('~')
        && !(path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        && !path.Split('/').Any(segment => segment is "" or "." or "..");
}

/// <summary>
/// Turns a model's proposal into what may be stored, by the rules of ADR-017. It decides from
/// three things only: the proposal, the set of cited locations that passed the citation check
/// (in the snapshot, retrieved in this run, not blank), and which of the proposed paths are
/// files of the snapshot. It has no way to accept something the server did not verify.
/// <list type="bullet">
/// <item>An entry is kept only if every citation on it passed. One failed citation removes the
/// entry: nothing is kept with a part of its evidence.</item>
/// <item>The summary and each step, risk, verification entry and modified or deleted file also
/// need at least one citation. An unknown and a created file may have none.</item>
/// <item>A file's path must be clean, listed once, and exist in the snapshot unless it is
/// marked <c>create</c>, in which case it must not exist there.</item>
/// <item>A summary that fails is replaced by a fixed sentence, because a plan has to have one.</item>
/// <item>The proposal is a plan only if the model called its evidence sufficient, at least one
/// citation passed and at least one step is left. Otherwise the outcome is insufficient
/// evidence: no step, file, risk or verification entry is kept, only the unknowns, uncited.</item>
/// </list>
/// </summary>
public static class PlanAssembler
{
    public const string InsufficientEvidenceSummary =
        "TRACE could not find enough evidence in the indexed snapshot to plan this change.";

    public const string ReplacedSummary =
        "TRACE did not keep a summary from the model, because none was tied to source lines that could be verified. "
        + "The entries below are the ones whose citations were verified.";

    public const string ModelReportedInsufficient =
        "The model reported that the retrieved source was not enough to plan this change.";

    public const string NoCitationVerified =
        "None of the proposed plan's citations could be verified against the snapshot, so the plan was discarded.";

    public const string NoStepVerified =
        "No step of the proposed plan was tied to source lines that could be verified, so the plan was discarded.";

    public const string TokenLimitReached =
        "The investigation reached its token limit before the model produced a plan.";

    public const string StepLimitReached =
        "The investigation reached its step limit before the model produced a plan.";

    /// <param name="accepted">The cited locations that passed the citation check.</param>
    /// <param name="inSnapshot">For each clean proposed path, whether it is a file of the snapshot. A path not listed counts as unknown and fails.</param>
    public static AssembledPlan Assemble(
        ModelPlan proposal,
        IReadOnlySet<Citation> accepted,
        IReadOnlyDictionary<string, bool> inSnapshot)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(inSnapshot);

        var removals = new List<string>();
        var rejectedCitations = proposal.CitedLocations().Count(citation => !accepted.Contains(citation));

        var summary = Keep(proposal.Summary, "the summary", citationRequired: true, accepted, removals: null);
        var steps = KeepAll(proposal.Steps, "step", citationRequired: true, accepted, removals);
        var files = KeepFiles(proposal.FilesToChange, accepted, inSnapshot, removals);
        var risks = KeepAll(proposal.Risks, "risk", citationRequired: true, accepted, removals);
        var verification = KeepAll(proposal.Verification, "verification entry", citationRequired: true, accepted, removals);
        var unknowns = KeepAll(proposal.Unknowns, "unknown", citationRequired: false, accepted, removals);

        var summaryReplaced = summary is null;
        var trimmed = new PlanTrimmed(
            rejectedCitations,
            Count(proposal.Steps) - steps.Count,
            Count(proposal.FilesToChange) - files.Count,
            Count(proposal.Risks) - risks.Count,
            Count(proposal.Verification) - verification.Count,
            Count(proposal.Unknowns) - unknowns.Count,
            summaryReplaced);

        var reason = !proposal.SufficientEvidence
            ? ModelReportedInsufficient
            : accepted.Count == 0
                ? NoCitationVerified
                : steps.Count == 0
                    ? NoStepVerified
                    : null;

        if (reason is not null)
        {
            // Nothing of the proposal is a plan. The unknowns stay, as the model's words about
            // what it could not find, without any citation.
            return Insufficient(
                reason,
                [.. unknowns.Select(unknown => unknown with { Citations = [] })],
                trimmed with { SummaryReplaced = false },
                removals);
        }

        if (summaryReplaced)
        {
            removals.Insert(0, "Replaced the summary of the proposed plan: it was not tied to citations that could be verified.");
        }

        return new AssembledPlan(
            PlanOutcome.Planned,
            summary ?? new AssembledStatement(ReplacedSummary, []),
            steps,
            files,
            risks,
            verification,
            unknowns,
            trimmed,
            trimmed.Any ? [Describe(trimmed)] : [],
            removals);
    }

    /// <summary>The outcome of a run that ended without a proposal, or whose proposal was no plan.</summary>
    public static AssembledPlan Insufficient(
        string reason,
        IReadOnlyList<AssembledStatement>? unknowns = null,
        PlanTrimmed? trimmed = null,
        IReadOnlyList<string>? removals = null)
    {
        trimmed ??= PlanTrimmed.Nothing;
        return new AssembledPlan(
            PlanOutcome.InsufficientEvidence,
            new AssembledStatement(InsufficientEvidenceSummary, []),
            Steps: [],
            FilesToChange: [],
            Risks: [],
            Verification: [],
            unknowns ?? [],
            trimmed,
            trimmed.Any ? [reason, Describe(trimmed)] : [reason],
            removals ?? []);
    }

    /// <summary>One sentence of counts. It never repeats what was removed.</summary>
    public static string Describe(PlanTrimmed trimmed)
    {
        var parts = new List<string>();
        Add(parts, trimmed.Steps, "step(s)");
        Add(parts, trimmed.Files, "file(s)");
        Add(parts, trimmed.Risks, "risk(s)");
        Add(parts, trimmed.Verification, "verification entry(ies)");
        Add(parts, trimmed.Unknowns, "unknown(s)");
        if (trimmed.SummaryReplaced)
        {
            parts.Add("the summary");
        }

        var removed = parts.Count == 0 ? "nothing else" : string.Join(", ", parts);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"TRACE removed from the model's proposal what it could not verify: {removed}; {trimmed.Citations} cited location(s) were discarded.");
    }

    private static void Add(List<string> parts, int count, string name)
    {
        if (count > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {name}"));
        }
    }

    private static int Count<T>(IReadOnlyList<T>? items) => items?.Count ?? 0;

    private static List<AssembledStatement> KeepAll(
        IReadOnlyList<ModelPlanStatement>? statements,
        string name,
        bool citationRequired,
        IReadOnlySet<Citation> accepted,
        List<string> removals)
    {
        var kept = new List<AssembledStatement>();
        for (var index = 0; index < Count(statements); index++)
        {
            if (Keep(statements![index], $"{name} {index + 1}", citationRequired, accepted, removals) is { } statement)
            {
                kept.Add(statement);
            }
        }

        return kept;
    }

    private static AssembledStatement? Keep(
        ModelPlanStatement? statement,
        string name,
        bool citationRequired,
        IReadOnlySet<Citation> accepted,
        List<string>? removals)
    {
        var text = PlanText.Clean(statement?.Text);
        var citationProblem = CitationProblem(statement?.Citations, citationRequired, accepted, out var citations);
        var problem = text.Length == 0 ? "it has no text" : citationProblem;
        if (problem is not null)
        {
            removals?.Add($"Removed {name} of the proposed plan: {problem}.");
            return null;
        }

        return new AssembledStatement(text, citations);
    }

    private static List<AssembledFile> KeepFiles(
        IReadOnlyList<ModelPlanFile>? files,
        IReadOnlySet<Citation> accepted,
        IReadOnlyDictionary<string, bool> inSnapshot,
        List<string> removals)
    {
        var kept = new List<AssembledFile>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Count(files); index++)
        {
            var file = files![index];
            var change = file.Change switch
            {
                ModelPlan.Create => PlanFileChange.Create,
                ModelPlan.Delete => PlanFileChange.Delete,
                _ => PlanFileChange.Modify,
            };
            var reason = PlanText.Clean(file.Reason);
            var citations = (IReadOnlyList<Citation>)[];

            var problem = !PlanPaths.IsClean(file.Path)
                ? "its path is not a repository-relative path"
                : !listed.Add(file.Path!)
                    ? "its path is listed more than once"
                    : !inSnapshot.TryGetValue(file.Path!, out var exists)
                        ? "its path could not be checked against the snapshot"
                        : change == PlanFileChange.Create && exists
                            ? "it is marked as a new file but its path exists in the indexed snapshot"
                            : change != PlanFileChange.Create && !exists
                                ? "its path is not a file of the indexed snapshot and it is not marked as new"
                                : reason.Length == 0
                                    ? "it has no reason"
                                    : CitationProblem(file.Citations, change != PlanFileChange.Create, accepted, out citations);

            if (problem is not null)
            {
                removals.Add($"Removed file entry {index + 1} of the proposed plan: {problem}.");
                continue;
            }

            kept.Add(new AssembledFile(file.Path!, change, reason, citations));
        }

        return kept;
    }

    private static string? CitationProblem(
        IReadOnlyList<ModelPlanCitation>? claimed,
        bool citationRequired,
        IReadOnlySet<Citation> accepted,
        out IReadOnlyList<Citation> citations)
    {
        citations = [.. (claimed ?? []).Select(citation => citation.ToCitation()).Distinct()];
        return citations.Any(citation => !accepted.Contains(citation))
            ? "a citation it relies on was discarded"
            : citationRequired && citations.Count == 0
                ? "it has no citation"
                : null;
    }
}

/// <summary>Cleans a sentence a model wrote before it is stored: plain text, nothing PostgreSQL cannot hold.</summary>
public static class PlanText
{
    /// <summary>
    /// Removes NUL and carriage returns, replaces a lone surrogate, turns every other control
    /// character but a line feed into a space, and trims.
    /// </summary>
    public static string Clean(string? text) =>
        new string([.. Issues.IssueText.Clean(text, int.MaxValue, out _)
            .Where(character => character != '\r')
            .Select(character => char.IsControl(character) && character != '\n' ? ' ' : character)]).Trim();
}
