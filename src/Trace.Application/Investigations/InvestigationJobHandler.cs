using System.Text.Json;
using Trace.Application.Jobs;
using Trace.Domain;

namespace Trace.Application.Investigations;

/// <summary>Runs a queued investigation on a worker.</summary>
public sealed class InvestigationJobHandler(QuestionInvestigator investigator) : IJobHandler
{
    public const string JobKind = "investigation.run";

    public string Kind => JobKind;

    public static string Payload(Guid investigationId) =>
        JsonSerializer.Serialize(new InvestigationJobPayload(investigationId), JsonSerializerOptions.Web);

    public Task HandleAsync(JobContext context) =>
        investigator.RunAsync(
            InvestigationId(context.PayloadJson), context.Attempt, context.MaxAttempts, context.CancellationToken);

    public Task OnAbandonedAsync(
        Guid jobId,
        string payloadJson,
        JobStatus finalStatus,
        string? errorCode,
        CancellationToken cancellationToken) =>
        investigator.AbandonAsync(
            InvestigationId(payloadJson), cancelled: finalStatus == JobStatus.Cancelled, errorCode, cancellationToken);

    private static Guid InvestigationId(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<InvestigationJobPayload>(payloadJson, JsonSerializerOptions.Web)!.InvestigationId;
        }
        catch (Exception exception) when (exception is JsonException or NullReferenceException)
        {
            throw new JobFailedException(JobErrors.InvalidPayload, "The job payload is not a valid investigation reference.", exception);
        }
    }

    private sealed record InvestigationJobPayload(Guid InvestigationId);
}
