using System.Text.Json;
using System.Text.Json.Serialization;
using Trace.Domain;

namespace Trace.Application.Plans;

public enum PlanOutcome
{
    /// <summary>A plan with at least one step, every claim about existing code cited.</summary>
    Planned,

    /// <summary>No plan could be grounded in the snapshot: no steps, files, risks, verification or evidence.</summary>
    InsufficientEvidence,
}

public enum PlanFileChange
{
    Modify,
    Create,
    Delete,
}

/// <summary>
/// One validated source location a plan relies on, with the text that is there.
/// </summary>
/// <param name="Id">
/// The ID of the stored evidence reference (<c>evidence_refs.id</c>) of the plan's investigation.
/// This is what a citation is: every <c>citations</c> entry in a plan is one of these IDs.
/// </param>
/// <param name="StartLine">1-based, inclusive.</param>
/// <param name="EndLine">1-based, inclusive.</param>
/// <param name="Excerpt">The cited lines, read from the snapshot rather than taken from a model.</param>
public sealed record PlanEvidence(Guid Id, string Path, int StartLine, int EndLine, string Excerpt);

/// <summary>A sentence or paragraph of the plan and the evidence it rests on.</summary>
/// <param name="Citations">IDs of entries in <see cref="PlanResult.Evidence"/>.</param>
public sealed record PlanStatement(string Text, IReadOnlyList<Guid> Citations);

/// <param name="Path">Repository-relative, forward slashes. For <c>create</c> a path that does not exist yet.</param>
/// <param name="Citations">IDs of entries in <see cref="PlanResult.Evidence"/>.</param>
public sealed record PlanFile(string Path, PlanFileChange Change, string Reason, IReadOnlyList<Guid> Citations);

/// <summary>A stored plan result is not one this code can read, or a plan is not valid to store.</summary>
public sealed class PlanSchemaException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// The result stored on a finished plan (<c>plans.result_json</c>): the versioned contract
/// documented in <c>docs/plan-result-schema.md</c>. It can only be written and read in a valid
/// state: <see cref="ToJson"/> and <see cref="FromJson"/> both refuse anything
/// <see cref="Validate"/> finds a problem with, and reading refuses unknown fields, missing
/// fields and any other schema version.
/// <para>
/// This type checks the shape. Whether the cited lines exist in the snapshot and were retrieved
/// during the run is decided by whoever builds the result (the plan workflow); that the IDs are
/// evidence rows of the same investigation is checked again by the database.
/// </para>
/// </summary>
/// <param name="Steps">In the order they should be carried out.</param>
public sealed record PlanResult(
    string SchemaVersion,
    PlanOutcome Outcome,
    PlanStatement Summary,
    IReadOnlyList<PlanStatement> Steps,
    IReadOnlyList<PlanFile> FilesToChange,
    IReadOnlyList<PlanStatement> Risks,
    IReadOnlyList<PlanStatement> Verification,
    IReadOnlyList<PlanStatement> Unknowns,
    IReadOnlyList<PlanEvidence> Evidence)
{
    /// <summary>
    /// The one version this code writes and reads. Any change to the name, type, meaning,
    /// requiredness or limit of a field makes a new version (<c>plan-v2</c>, ...); see the
    /// schema document for the rule.
    /// </summary>
    public const string CurrentSchemaVersion = "plan-v1";

    public const int MaxSummaryLength = 2_000;
    public const int MaxSteps = 30;
    public const int MaxStepLength = 2_000;
    public const int MaxFiles = 50;
    public const int MaxRisks = 20;
    public const int MaxVerification = 20;
    public const int MaxUnknowns = 20;

    /// <summary>A file's reason, a risk, a verification step or an unknown.</summary>
    public const int MaxShortTextLength = 1_000;

    public const int MaxCitationsPerItem = 10;

    /// <summary>The same number of citations one investigation may have validated.</summary>
    public const int MaxEvidence = Retrieval.CitationValidator.MaxCitations;

    /// <summary>The same range one read or one citation may span.</summary>
    public const int MaxEvidenceLines = Retrieval.SourceTools.MaxRangeLines;

    /// <summary>All excerpts of one plan together, in characters.</summary>
    public const int MaxTotalExcerptLength = 1_000_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <exception cref="PlanSchemaException">The plan is not valid; the message lists every problem.</exception>
    public string ToJson()
    {
        EnsureValid();
        var json = JsonSerializer.Serialize(this, Json);
        return json.Length <= Plan.MaxResultLength
            ? json
            : throw new PlanSchemaException($"The plan is larger than {Plan.MaxResultLength} characters of JSON.");
    }

    /// <exception cref="PlanSchemaException">
    /// The text is not JSON, is of another schema version, has an unknown or a missing field, or
    /// breaks a rule of the schema.
    /// </exception>
    public static PlanResult FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        string? version;
        try
        {
            using var document = JsonDocument.Parse(json);
            version = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var property)
                && property.ValueKind == JsonValueKind.String
                    ? property.GetString()
                    : null;
        }
        catch (JsonException exception)
        {
            throw new PlanSchemaException("The stored plan is not valid JSON.", exception);
        }

        // Checked before anything else is read: another version may differ in any other field.
        if (version != CurrentSchemaVersion)
        {
            throw new PlanSchemaException(
                $"The stored plan has schema version \"{version}\"; this build reads only \"{CurrentSchemaVersion}\".");
        }

        PlanResult result;
        try
        {
            result = JsonSerializer.Deserialize<PlanResult>(json, Json)
                ?? throw new PlanSchemaException("The stored plan is null.");
        }
        catch (JsonException exception)
        {
            throw new PlanSchemaException($"The stored plan does not follow schema {CurrentSchemaVersion}.", exception);
        }

        result.EnsureValid();
        return result;
    }

    public void EnsureValid()
    {
        var problems = Validate();
        if (problems.Count > 0)
        {
            throw new PlanSchemaException("The plan is not valid: " + string.Join(" ", problems));
        }
    }

    /// <summary>Every rule of the schema this plan breaks; empty when it is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SchemaVersion != CurrentSchemaVersion)
        {
            problems.Add($"schemaVersion must be \"{CurrentSchemaVersion}\".");
        }

        if (!Enum.IsDefined(Outcome))
        {
            problems.Add("outcome is not a known value.");
        }

        if (Summary is null || Steps is null || FilesToChange is null || Risks is null
            || Verification is null || Unknowns is null || Evidence is null)
        {
            problems.Add("Every field is required.");
            return problems;
        }

        var evidenceIds = CheckEvidence(problems);
        var cited = new HashSet<Guid>();

        CheckStatement(problems, "summary", Summary, MaxSummaryLength, citationRequired: false, evidenceIds, cited);
        CheckStatements(problems, "steps", Steps, MaxSteps, MaxStepLength, citationRequired: true, evidenceIds, cited);
        CheckStatements(problems, "risks", Risks, MaxRisks, MaxShortTextLength, citationRequired: true, evidenceIds, cited);
        CheckStatements(
            problems, "verification", Verification, MaxVerification, MaxShortTextLength, citationRequired: true, evidenceIds, cited);
        CheckStatements(problems, "unknowns", Unknowns, MaxUnknowns, MaxShortTextLength, citationRequired: false, evidenceIds, cited);
        CheckFiles(problems, evidenceIds, cited);

        if (evidenceIds.Except(cited).Any())
        {
            problems.Add("evidence has an entry that nothing cites.");
        }

        if (Outcome == PlanOutcome.Planned && Steps.Count == 0)
        {
            problems.Add("A planned outcome needs at least one step.");
        }

        if (Outcome == PlanOutcome.InsufficientEvidence
            && (Steps.Count > 0 || FilesToChange.Count > 0 || Risks.Count > 0 || Verification.Count > 0 || Evidence.Count > 0))
        {
            problems.Add("An insufficient_evidence outcome has no steps, files, risks, verification or evidence.");
        }

        return problems;
    }

    private HashSet<Guid> CheckEvidence(List<string> problems)
    {
        var ids = new HashSet<Guid>();
        if (Evidence.Count > MaxEvidence)
        {
            problems.Add($"evidence has more than {MaxEvidence} entries.");
        }

        long totalExcerpt = 0;
        for (var index = 0; index < Evidence.Count; index++)
        {
            var name = $"evidence[{index}]";
            if (Evidence[index] is not { } entry)
            {
                problems.Add($"{name} is null.");
                continue;
            }

            if (entry.Id == Guid.Empty || !ids.Add(entry.Id))
            {
                problems.Add($"{name}.id is empty or repeated.");
            }

            CheckPath(problems, $"{name}.path", entry.Path);
            if (entry.StartLine < 1 || entry.EndLine < entry.StartLine || entry.EndLine - entry.StartLine + 1 > MaxEvidenceLines)
            {
                problems.Add($"{name} must cite 1 to {MaxEvidenceLines} lines, numbered from 1.");
            }

            if (string.IsNullOrWhiteSpace(entry.Excerpt) || entry.Excerpt.Contains('\0'))
            {
                problems.Add($"{name}.excerpt is blank or contains a NUL character.");
            }

            totalExcerpt += entry.Excerpt?.Length ?? 0;
        }

        if (totalExcerpt > MaxTotalExcerptLength)
        {
            problems.Add($"The excerpts together are longer than {MaxTotalExcerptLength} characters.");
        }

        return ids;
    }

    private void CheckFiles(List<string> problems, HashSet<Guid> evidenceIds, HashSet<Guid> cited)
    {
        if (FilesToChange.Count > MaxFiles)
        {
            problems.Add($"filesToChange has more than {MaxFiles} entries.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < FilesToChange.Count; index++)
        {
            var name = $"filesToChange[{index}]";
            if (FilesToChange[index] is not { } file)
            {
                problems.Add($"{name} is null.");
                continue;
            }

            CheckPath(problems, $"{name}.path", file.Path);
            if (file.Path is not null && !paths.Add(file.Path))
            {
                problems.Add($"{name}.path is listed twice.");
            }

            if (!Enum.IsDefined(file.Change))
            {
                problems.Add($"{name}.change is not a known value.");
            }

            CheckText(problems, $"{name}.reason", file.Reason, MaxShortTextLength);

            // A file that does not exist yet has no lines of its own to cite.
            CheckCitations(
                problems, name, file.Citations, citationRequired: file.Change != PlanFileChange.Create, evidenceIds, cited);
        }
    }

    private static void CheckStatements(
        List<string> problems,
        string name,
        IReadOnlyList<PlanStatement> statements,
        int maxCount,
        int maxLength,
        bool citationRequired,
        HashSet<Guid> evidenceIds,
        HashSet<Guid> cited)
    {
        if (statements.Count > maxCount)
        {
            problems.Add($"{name} has more than {maxCount} entries.");
        }

        for (var index = 0; index < statements.Count; index++)
        {
            CheckStatement(problems, $"{name}[{index}]", statements[index], maxLength, citationRequired, evidenceIds, cited);
        }
    }

    private static void CheckStatement(
        List<string> problems,
        string name,
        PlanStatement? statement,
        int maxLength,
        bool citationRequired,
        HashSet<Guid> evidenceIds,
        HashSet<Guid> cited)
    {
        if (statement is null)
        {
            problems.Add($"{name} is null.");
            return;
        }

        CheckText(problems, $"{name}.text", statement.Text, maxLength);
        CheckCitations(problems, name, statement.Citations, citationRequired, evidenceIds, cited);
    }

    private static void CheckCitations(
        List<string> problems,
        string name,
        IReadOnlyList<Guid>? citations,
        bool citationRequired,
        HashSet<Guid> evidenceIds,
        HashSet<Guid> cited)
    {
        if (citations is null)
        {
            problems.Add($"{name}.citations is null.");
            return;
        }

        if (citationRequired && citations.Count == 0)
        {
            problems.Add($"{name} needs at least one citation.");
        }

        if (citations.Count > MaxCitationsPerItem)
        {
            problems.Add($"{name} has more than {MaxCitationsPerItem} citations.");
        }

        if (citations.Distinct().Count() != citations.Count)
        {
            problems.Add($"{name} cites the same evidence twice.");
        }

        if (!citations.All(evidenceIds.Contains))
        {
            problems.Add($"{name} cites something that is not in evidence.");
        }

        cited.UnionWith(citations);
    }

    private static void CheckText(List<string> problems, string name, string? text, int maxLength)
    {
        // NUL cannot be stored in PostgreSQL, in text or in jsonb.
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength || text.Contains('\0'))
        {
            problems.Add($"{name} must be 1 to {maxLength} characters, not blank, without a NUL character.");
        }
    }

    private static void CheckPath(List<string> problems, string name, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > SourceChunk.MaxPathLength
            || path.Contains('\\')
            || path.Any(char.IsControl)
            || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            problems.Add($"{name} must be a repository-relative path with forward slashes and no empty, '.' or '..' segments.");
        }
    }
}
