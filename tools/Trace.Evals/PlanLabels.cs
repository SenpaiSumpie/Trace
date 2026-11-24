using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Trace.Application.Plans;

namespace Trace.Evals;

/// <summary>The label file is missing, malformed or breaks one of the format's rules.</summary>
public sealed class PlanLabelsException(string message) : Exception(message);

/// <summary>
/// One way to satisfy a required file: a path listed with a kind of change, or a new file
/// created under a directory. Exactly one of the two.
/// </summary>
/// <param name="Path">A file of the repository, or one to be created at exactly this path.</param>
/// <param name="Change"><c>modify</c>, <c>create</c> or <c>delete</c>; set together with <paramref name="Path"/>.</param>
/// <param name="CreateUnder">A directory prefix ending in <c>/</c>: any file the plan creates below it counts.</param>
public sealed record PlanFileAlternative(string? Path, string? Change, string? CreateUnder);

/// <summary>A file a correct plan must touch. It is matched when any one alternative is.</summary>
/// <param name="Why">For whoever maintains the labels: what in the source makes this file necessary.</param>
public sealed record PlanRequiredFile(IReadOnlyList<PlanFileAlternative> AnyOf, string? Why);

/// <summary>A file a plan may touch without it counting against precision. Exactly one of path and prefix.</summary>
/// <param name="Change">Null allows any kind of change.</param>
public sealed record PlanOptionalFile(string? Path, string? Under, string? Change);

/// <summary>One thing a person has to look for in a plan. Its <paramref name="Id"/> is what a rating refers to.</summary>
public sealed record PlanLabelItem(string Id, string Text);

/// <summary>
/// What is expected of the plan for one issue. Copied into the result file, so a result can be
/// scored without the label file.
/// </summary>
/// <param name="ExpectedOutcome">One of <see cref="PlanLabels.ExpectPlanned"/>, <see cref="PlanLabels.ExpectInsufficientEvidence"/>, <see cref="PlanLabels.ExpectAny"/>.</param>
/// <param name="ForbiddenPaths">Paths, or prefixes ending in <c>/</c>, a plan must neither list nor cite.</param>
/// <param name="ForbiddenText">Exact strings that must not occur in a step, a file entry or a verification entry.</param>
/// <param name="TestChangeUnder">Set when the change needs a test: the plan must list a file under this prefix.</param>
/// <param name="Points">Rated by a person: what the plan must say (completeness).</param>
/// <param name="Risks">Rated by a person: risks the plan must mention (safety).</param>
/// <param name="Verification">Rated by a person: what the plan's verification must cover.</param>
/// <param name="Safety">Rated by a person: other safety expectations.</param>
public sealed record PlanIssueLabel(
    int Number,
    string Split,
    string ExpectedOutcome,
    IReadOnlyList<PlanRequiredFile> RequiredFiles,
    IReadOnlyList<PlanOptionalFile> OptionalFiles,
    IReadOnlyList<string> ForbiddenPaths,
    IReadOnlyList<string> ForbiddenText,
    string? TestChangeUnder,
    IReadOnlyList<PlanLabelItem> Points,
    IReadOnlyList<PlanLabelItem> Risks,
    IReadOnlyList<PlanLabelItem> Verification,
    IReadOnlyList<PlanLabelItem> Safety,
    string? Notes)
{
    /// <summary>Every item a person rates, in the order of the rating sheet.</summary>
    public IEnumerable<PlanLabelItem> HumanItems() => Points.Concat(Risks).Concat(Safety).Concat(Verification);
}

/// <summary>
/// Labeled issues of one repository at one revision, for issue-to-plan evaluation. The format
/// is documented in <c>docs/evaluation.md</c>; <c>tests/fixtures/sample-repo.plan-labels.json</c>
/// is the fixture's file.
/// </summary>
public sealed partial record PlanLabels(string Name, string Version, string? Revision, IReadOnlyList<PlanIssueLabel> Issues)
{
    public const string ExpectPlanned = "planned";
    public const string ExpectInsufficientEvidence = "insufficient_evidence";

    /// <summary>Either outcome is acceptable; the issue is scored on safety only.</summary>
    public const string ExpectAny = "any";

    public const string FileSuffix = ".plan-labels.json";
    public const int MaxItemLength = 1_000;

    // A typo in a label file should fail loudly, not silently drop a label.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly string[] Changes = [ModelPlan.Modify, ModelPlan.Create, ModelPlan.Delete];

    public static PlanLabels Load(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PlanLabelsException($"Could not read the label file '{path}': {exception.Message}");
        }

        var fileName = Path.GetFileName(path);
        return Parse(
            json,
            fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)
                ? fileName[..^FileSuffix.Length]
                : Path.GetFileNameWithoutExtension(fileName));
    }

    /// <param name="fallbackName">Used as the name when the file has no <c>name</c>.</param>
    public static PlanLabels Parse(string json, string fallbackName)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException exception)
        {
            throw new PlanLabelsException($"The label file is not valid JSON of the documented shape: {exception.Message}");
        }

        if (file is null)
        {
            throw new PlanLabelsException("The label file is empty.");
        }

        var name = file.Name ?? fallbackName;
        if (!NamePattern().IsMatch(name))
        {
            throw new PlanLabelsException(
                $"Label set name '{name}' must be 1-64 characters of a-z, 0-9, '.', '_' or '-', starting with a letter or digit. Set \"name\" in the file.");
        }

        if (string.IsNullOrWhiteSpace(file.LabelsVersion) || file.LabelsVersion.Length > EvalDataset.MaxVersionLength)
        {
            throw new PlanLabelsException($"\"labelsVersion\" is required and must be at most {EvalDataset.MaxVersionLength} characters.");
        }

        if (file.Revision is not null && !RevisionPattern().IsMatch(file.Revision))
        {
            throw new PlanLabelsException("\"revision\" must be a full 40-character lowercase hexadecimal revision ID.");
        }

        if (file.Issues is not { Count: > 0 })
        {
            throw new PlanLabelsException("\"issues\" must contain at least one issue.");
        }

        var issues = new List<PlanIssueLabel>(file.Issues.Count);
        var numbers = new HashSet<int>();
        for (var index = 0; index < file.Issues.Count; index++)
        {
            var issue = ParseIssue(file.Issues[index], index);
            if (!numbers.Add(issue.Number))
            {
                throw new PlanLabelsException($"Issue #{issue.Number} is labeled more than once.");
            }

            issues.Add(issue);
        }

        return new PlanLabels(name, file.LabelsVersion, file.Revision, issues);
    }

    /// <summary>Whether a path is the forbidden or allowed path itself, or lies under it when it is a prefix ending in <c>/</c>.</summary>
    public static bool IsAtOrUnder(string path, string pathOrPrefix) =>
        pathOrPrefix.EndsWith('/')
            ? path.StartsWith(pathOrPrefix, StringComparison.Ordinal)
            : string.Equals(path, pathOrPrefix, StringComparison.Ordinal);

    private static PlanIssueLabel ParseIssue(IssueDto? dto, int index)
    {
        if (dto is null || dto.Number is not >= 1)
        {
            throw new PlanLabelsException($"issues[{index}].number must be an issue number of 1 or more.");
        }

        var where = $"Issue #{dto.Number}";
        if (dto.Split is not (EvalDataset.DevSplit or EvalDataset.TestSplit))
        {
            throw new PlanLabelsException($"{where}: \"split\" must be \"{EvalDataset.DevSplit}\" or \"{EvalDataset.TestSplit}\".");
        }

        if (dto.ExpectedOutcome is not (ExpectPlanned or ExpectInsufficientEvidence or ExpectAny))
        {
            throw new PlanLabelsException(
                $"{where}: \"expectedOutcome\" must be \"{ExpectPlanned}\", \"{ExpectInsufficientEvidence}\" or \"{ExpectAny}\".");
        }

        var required = (dto.RequiredFiles ?? []).Select(file => ParseRequired(file, where)).ToList();
        var optional = (dto.OptionalFiles ?? []).Select(file => ParseOptional(file, where)).ToList();
        var forbiddenPaths = (dto.ForbiddenPaths ?? []).Select(path => RequirePathOrPrefix(path, where, "forbiddenPaths")).ToList();
        var forbiddenText = (dto.ForbiddenText ?? []).ToList();
        if (forbiddenText.Any(text => text is null || text.Trim().Length < 4 || text.Length > MaxItemLength))
        {
            throw new PlanLabelsException($"{where}: every \"forbiddenText\" entry must be 4 to {MaxItemLength} characters.");
        }

        if (dto.TestChangeUnder is not null)
        {
            RequirePrefix(dto.TestChangeUnder, where, "testChangeUnder");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var points = ParseItems(dto.Points, where, "points", ids);
        var risks = ParseItems(dto.Risks, where, "risks", ids);
        var verification = ParseItems(dto.Verification, where, "verification", ids);
        var safety = ParseItems(dto.Safety, where, "safety", ids);

        switch (dto.ExpectedOutcome)
        {
            case ExpectPlanned when required.Count == 0 || points.Count == 0 || risks.Count == 0 || verification.Count == 0:
                throw new PlanLabelsException(
                    $"{where}: an issue expected to be planned needs at least one of each of \"requiredFiles\", \"points\", \"risks\" and \"verification\".");
            case ExpectInsufficientEvidence when required.Count + optional.Count + ids.Count > 0 || dto.TestChangeUnder is not null:
                throw new PlanLabelsException(
                    $"{where}: an issue expected to end as insufficient evidence must not label files or rated items; say in \"notes\" why it cannot be planned.");
            case ExpectInsufficientEvidence when string.IsNullOrWhiteSpace(dto.Notes):
                throw new PlanLabelsException($"{where}: say in \"notes\" why the repository gives too little evidence to plan.");
            case ExpectAny when required.Count + points.Count + risks.Count + verification.Count > 0 || dto.TestChangeUnder is not null:
                throw new PlanLabelsException(
                    $"{where}: an issue with expected outcome \"{ExpectAny}\" is scored on safety only; it may label \"optionalFiles\", \"forbiddenPaths\", \"forbiddenText\" and \"safety\".");
            case ExpectAny when forbiddenPaths.Count + forbiddenText.Count + safety.Count == 0:
                throw new PlanLabelsException($"{where}: an issue with expected outcome \"{ExpectAny}\" needs at least one safety expectation.");
        }

        return new PlanIssueLabel(
            dto.Number.Value, dto.Split, dto.ExpectedOutcome, required, optional, forbiddenPaths, forbiddenText!,
            dto.TestChangeUnder, points, risks, verification, safety, dto.Notes);
    }

    private static PlanRequiredFile ParseRequired(RequiredDto? dto, string where)
    {
        if (dto?.AnyOf is not { Count: > 0 })
        {
            throw new PlanLabelsException($"{where}: every \"requiredFiles\" entry needs \"anyOf\" with at least one alternative.");
        }

        foreach (var alternative in dto.AnyOf)
        {
            var isPath = alternative is { Path: not null, CreateUnder: null } && Changes.Contains(alternative.Change);
            var isPrefix = alternative is { Path: null, Change: null, CreateUnder: not null };
            if (!isPath && !isPrefix)
            {
                throw new PlanLabelsException(
                    $"{where}: a required file alternative is either \"path\" with \"change\" (modify, create or delete) or \"createUnder\" alone.");
            }

            if (isPath)
            {
                RequirePath(alternative!.Path, where, "requiredFiles");
            }
            else
            {
                RequirePrefix(alternative!.CreateUnder, where, "createUnder");
            }
        }

        return new PlanRequiredFile([.. dto.AnyOf!], dto.Why);
    }

    private static PlanOptionalFile ParseOptional(PlanOptionalFile? dto, string where)
    {
        if (dto is null || (dto.Path is null) == (dto.Under is null) || (dto.Change is not null && !Changes.Contains(dto.Change)))
        {
            throw new PlanLabelsException(
                $"{where}: every \"optionalFiles\" entry needs exactly one of \"path\" and \"under\", and \"change\" may only be modify, create or delete.");
        }

        if (dto.Path is not null)
        {
            RequirePath(dto.Path, where, "optionalFiles");
        }
        else
        {
            RequirePrefix(dto.Under, where, "under");
        }

        return dto;
    }

    private static List<PlanLabelItem> ParseItems(List<PlanLabelItem?>? items, string where, string name, HashSet<string> ids)
    {
        var parsed = new List<PlanLabelItem>();
        foreach (var item in items ?? [])
        {
            if (item?.Id is null || !ItemIdPattern().IsMatch(item.Id) || string.IsNullOrWhiteSpace(item.Text) || item.Text.Length > MaxItemLength)
            {
                throw new PlanLabelsException(
                    $"{where}: every \"{name}\" entry needs an \"id\" (a letter, then up to 19 of a-z, 0-9 or '-') and a \"text\" of at most {MaxItemLength} characters.");
            }

            if (!ids.Add(item.Id))
            {
                throw new PlanLabelsException($"{where}: item id '{item.Id}' is used more than once.");
            }

            parsed.Add(item);
        }

        return parsed;
    }

    private static void RequirePath(string? path, string where, string name)
    {
        if (!PlanPaths.IsClean(path))
        {
            throw new PlanLabelsException($"{where}: a \"{name}\" path must be repository-relative with forward slashes.");
        }
    }

    private static void RequirePrefix(string? prefix, string where, string name)
    {
        if (prefix is not { Length: > 1 } || !prefix.EndsWith('/') || !PlanPaths.IsClean(prefix[..^1]))
        {
            throw new PlanLabelsException($"{where}: \"{name}\" must be a repository-relative directory with forward slashes, ending in '/'.");
        }
    }

    private static string RequirePathOrPrefix(string? value, string where, string name)
    {
        if (value is not null && value.EndsWith('/'))
        {
            RequirePrefix(value, where, name);
        }
        else
        {
            RequirePath(value, where, name);
        }

        return value!;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,19}\z")]
    private static partial Regex ItemIdPattern();

    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex RevisionPattern();

    private sealed record FileDto(string? Name, string? LabelsVersion, string? Description, string? Revision, List<IssueDto?>? Issues);

    private sealed record IssueDto(
        int? Number,
        string? Split,
        string? ExpectedOutcome,
        List<RequiredDto?>? RequiredFiles,
        List<PlanOptionalFile?>? OptionalFiles,
        List<string?>? ForbiddenPaths,
        List<string?>? ForbiddenText,
        string? TestChangeUnder,
        List<PlanLabelItem?>? Points,
        List<PlanLabelItem?>? Risks,
        List<PlanLabelItem?>? Verification,
        List<PlanLabelItem?>? Safety,
        string? Notes);

    private sealed record RequiredDto(List<PlanFileAlternative?>? AnyOf, string? Why);
}
