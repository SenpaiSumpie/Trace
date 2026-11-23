using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Application.Retrieval;
using Trace.Domain;

namespace Trace.Application.Plans;

public sealed record ModelPlanCitation(string? Path, int StartLine, int EndLine)
{
    /// <summary>The claimed location, as the citation check takes it.</summary>
    public Citation ToCitation() => new(Path ?? string.Empty, StartLine, EndLine);
}

public sealed record ModelPlanStatement(string? Text, IReadOnlyList<ModelPlanCitation>? Citations);

public sealed record ModelPlanFile(string? Path, string? Change, string? Reason, IReadOnlyList<ModelPlanCitation>? Citations);

/// <summary>
/// The plan a model proposed in its final turn: parsed and bounded, nothing in it checked
/// against the snapshot yet. It is a proposal only. What is stored is what
/// <see cref="PlanAssembler"/> keeps of it.
/// <para>
/// Parsing is all or nothing. Text that is not JSON of this shape, a value of the wrong type
/// (for example an ID where a citation's location belongs), an unknown kind of change, or more
/// of anything than the stored plan may hold makes the whole output unusable
/// (<see cref="InvalidModelOutputException"/>): the investigation fails as
/// <c>invalid_model_output</c> and no part of the proposal is kept.
/// </para>
/// </summary>
public sealed record ModelPlan(
    bool SufficientEvidence,
    ModelPlanStatement? Summary,
    IReadOnlyList<ModelPlanStatement>? Steps,
    IReadOnlyList<ModelPlanFile>? FilesToChange,
    IReadOnlyList<ModelPlanStatement>? Risks,
    IReadOnlyList<ModelPlanStatement>? Verification,
    IReadOnlyList<ModelPlanStatement>? Unknowns)
{
    public const string Modify = "modify";
    public const string Create = "create";
    public const string Delete = "delete";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every location the proposal cites, each once, in the order first cited.</summary>
    public IReadOnlyList<Citation> CitedLocations() =>
        [.. new[] { Summary?.Citations }
            .Concat((Steps ?? []).Select(step => step.Citations))
            .Concat((FilesToChange ?? []).Select(file => file.Citations))
            .Concat((Risks ?? []).Select(risk => risk.Citations))
            .Concat((Verification ?? []).Select(entry => entry.Citations))
            .Concat((Unknowns ?? []).Select(unknown => unknown.Citations))
            .SelectMany(citations => citations ?? [])
            .Select(citation => citation.ToCitation())
            .Distinct()];

    /// <exception cref="InvalidModelOutputException">The text is not a usable proposal. The message is fixed text.</exception>
    public static ModelPlan Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidModelOutputException("The model returned no plan.");
        }

        ModelPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<ModelPlan>(ModelAnswer.StripCodeFence(text), Json);
        }
        catch (JsonException)
        {
            // The parser's own message can quote the model's text: it is not passed on.
            throw new InvalidModelOutputException("The model's plan was not valid JSON of the required shape.");
        }

        if (plan is null)
        {
            throw new InvalidModelOutputException("The model's plan was empty.");
        }

        CheckStatement(plan.Summary, PlanResult.MaxSummaryLength);
        CheckStatements(plan.Steps, PlanResult.MaxSteps, PlanResult.MaxStepLength);
        CheckStatements(plan.Risks, PlanResult.MaxRisks, PlanResult.MaxShortTextLength);
        CheckStatements(plan.Verification, PlanResult.MaxVerification, PlanResult.MaxShortTextLength);
        CheckStatements(plan.Unknowns, PlanResult.MaxUnknowns, PlanResult.MaxShortTextLength);

        if (plan.FilesToChange?.Count > PlanResult.MaxFiles)
        {
            throw TooMuch();
        }

        foreach (var file in plan.FilesToChange ?? [])
        {
            if (file is null || file.Change is not (Modify or Create or Delete))
            {
                throw new InvalidModelOutputException("The model's plan has a file entry that is not modify, create or delete.");
            }

            if (file.Path?.Length > SourceChunk.MaxPathLength || file.Reason?.Length > PlanResult.MaxShortTextLength)
            {
                throw TooMuch();
            }

            CheckCitations(file.Citations);
        }

        if (plan.CitedLocations().Count > PlanResult.MaxEvidence)
        {
            throw TooMuch();
        }

        return plan;
    }

    private static void CheckStatements(IReadOnlyList<ModelPlanStatement>? statements, int maxCount, int maxLength)
    {
        if (statements?.Count > maxCount)
        {
            throw TooMuch();
        }

        foreach (var statement in statements ?? [])
        {
            if (statement is null)
            {
                throw new InvalidModelOutputException("The model's plan has an empty entry.");
            }

            CheckStatement(statement, maxLength);
        }
    }

    private static void CheckStatement(ModelPlanStatement? statement, int maxLength)
    {
        if (statement?.Text?.Length > maxLength)
        {
            throw TooMuch();
        }

        CheckCitations(statement?.Citations);
    }

    private static void CheckCitations(IReadOnlyList<ModelPlanCitation>? citations)
    {
        if (citations?.Count > PlanResult.MaxCitationsPerItem)
        {
            throw TooMuch();
        }

        if (citations?.Any(citation => citation is null || citation.Path?.Length > SourceChunk.MaxPathLength) == true)
        {
            throw new InvalidModelOutputException("The model's plan has a citation that is not a location.");
        }
    }

    private static InvalidModelOutputException TooMuch() =>
        new("The model's plan was larger than a plan may be.");
}
