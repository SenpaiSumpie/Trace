using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trace.Application.Investigations;

namespace Trace.Application.Plans;

/// <summary>What a plan run produced, in numbers, and what validation removed on the way.</summary>
/// <param name="PlanId">The plan the run finished (<c>plans.id</c>).</param>
/// <param name="Steps">Steps in the stored plan.</param>
/// <param name="Evidence">Validated evidence references the stored plan cites.</param>
public sealed record PlanRunDetails(Guid PlanId, int IssueNumber, PlanOutcome Outcome, int Steps, int Evidence, PlanTrimmed Trimmed);

/// <summary>
/// What a completed issue-to-plan investigation stores in <c>investigations.result_json</c>.
/// The plan itself is in <c>plans.result_json</c>; this is the run's own account of it.
/// <para>
/// It is a valid <see cref="InvestigationResult"/> (schema version 1) with one more member,
/// <c>plan</c>, so everything that reads an investigation's result keeps working for a plan
/// investigation: <c>answer</c> is a fixed sentence written by the server, <c>evidence</c> is
/// empty (a plan's evidence is in the plan), <c>unknowns</c> is empty, and
/// <c>limitations</c> says in fixed sentences why the outcome is what it is and what was
/// removed. No text from the model or the issue is in it. The investigation outcome
/// <c>answered</c> stands for <c>planned</c>.
/// </para>
/// </summary>
public sealed record PlanRunSummary(
    int SchemaVersion,
    InvestigationOutcome Outcome,
    string Answer,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Limitations,
    PlanRunDetails Plan)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static PlanRunSummary For(Guid planId, int issueNumber, AssembledPlan plan, int evidenceCount)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var planned = plan.Outcome == PlanOutcome.Planned;
        return new PlanRunSummary(
            InvestigationResult.CurrentSchemaVersion,
            planned ? InvestigationOutcome.Answered : InvestigationOutcome.InsufficientEvidence,
            planned
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"TRACE made a plan for issue #{issueNumber} with {plan.Steps.Count} step(s) resting on {evidenceCount} validated evidence reference(s). The plan is stored with this investigation.")
                : PlanAssembler.InsufficientEvidenceSummary,
            Evidence: [],
            Unknowns: [],
            plan.Limitations,
            new PlanRunDetails(planId, issueNumber, plan.Outcome, plan.Steps.Count, evidenceCount, plan.Trimmed));
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static PlanRunSummary FromJson(string json) =>
        JsonSerializer.Deserialize<PlanRunSummary>(json, Json) is { Plan: not null } summary
            ? summary
            : throw new JsonException("The stored result is not that of a plan investigation.");
}
