using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Contracts.V1;
using Trace.Domain;

namespace Trace.Api;

/// <summary>
/// <c>/api/v1</c>. There is no authentication yet, so these endpoints only ever expose
/// repositories marked public (the demo fixture); everything else answers 404.
/// </summary>
internal static class V1Endpoints
{
    private const string QuestionType = "question";

    public static void MapV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/repositories", ListRepositoriesAsync);
        v1.MapPost("/investigations", CreateInvestigationAsync);
        v1.MapGet("/investigations/{id:guid}", GetInvestigationAsync).WithName(nameof(GetInvestigationAsync));
        v1.MapGet("/investigations/{id:guid}/events", GetInvestigationEventsAsync);
    }

    private static async Task<IResult> ListRepositoriesAsync(
        IRepositoryCatalog repositories,
        CancellationToken cancellationToken)
    {
        var visible = await repositories.ListVisibleAsync(cancellationToken);

        return TypedResults.Ok(visible.Select(repository => new RepositoryResponse(
            repository.Id,
            repository.Owner,
            repository.Name,
            repository.DefaultBranch,
            repository.LatestSnapshot is { } snapshot
                ? new SnapshotResponse(snapshot.Id, snapshot.CommitSha, snapshot.Branch, snapshot.CompletedAt)
                : null)));
    }

    private static async Task<IResult> CreateInvestigationAsync(
        CreateInvestigationRequest request,
        QuestionInvestigator investigator,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            var record = await investigator.AskAsync(
                new AskQuestion(request.RepositoryId, request.SnapshotId, request.Question!.Trim(), request.IdempotencyKey),
                cancellationToken);

            return TypedResults.CreatedAtRoute(
                ToResponse(record), nameof(GetInvestigationAsync), new { id = record.Investigation.Id });
        }
        catch (InvestigationRequestException exception)
        {
            return TypedResults.Problem(
                detail: exception.Message,
                statusCode: exception.Error switch
                {
                    InvestigationRequestError.RepositoryNotFound => StatusCodes.Status404NotFound,
                    InvestigationRequestError.SnapshotNotFound => StatusCodes.Status404NotFound,
                    _ => StatusCodes.Status409Conflict,
                });
        }
    }

    private static async Task<IResult> GetInvestigationAsync(
        Guid id,
        IInvestigationStore store,
        CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken) is { } record
            ? TypedResults.Ok(ToResponse(record))
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> GetInvestigationEventsAsync(
        Guid id,
        IInvestigationStore store,
        CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken) is { } record
            ? TypedResults.Ok(record.Events.Select(e => new InvestigationEventResponse(
                e.Seq, Snake(e.EventType), e.ToolName, e.Summary, e.CreatedAt)))
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    private static Dictionary<string, string[]> Validate(CreateInvestigationRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.RepositoryId == Guid.Empty)
        {
            errors["repositoryId"] = ["repositoryId is required."];
        }

        if (request.SnapshotId == Guid.Empty)
        {
            errors["snapshotId"] = ["snapshotId is required."];
        }

        if (request.Type != QuestionType)
        {
            errors["type"] = [$"type must be '{QuestionType}'; other investigation types are not supported yet."];
        }

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > Investigation.MaxQuestionLength)
        {
            errors["question"] = [$"question must be 1 to {Investigation.MaxQuestionLength} characters."];
        }

        if (request.IdempotencyKey is { } key
            && (string.IsNullOrWhiteSpace(key) || key.Length > Investigation.MaxIdempotencyKeyLength))
        {
            errors["idempotencyKey"] = [$"idempotencyKey must be 1 to {Investigation.MaxIdempotencyKeyLength} characters when present."];
        }

        return errors;
    }

    private static InvestigationResponse ToResponse(InvestigationRecord record)
    {
        var investigation = record.Investigation;
        var result = investigation.ResultJson is null ? null : InvestigationResult.FromJson(investigation.ResultJson);

        return new InvestigationResponse(
            investigation.Id,
            investigation.RepositoryId,
            investigation.SnapshotId,
            record.CommitSha,
            Snake(investigation.Type),
            investigation.Question,
            Snake(investigation.State),
            investigation.ModelId,
            investigation.PromptVersion,
            investigation.CreatedAt,
            investigation.UpdatedAt,
            result is null
                ? null
                : new InvestigationResultResponse(
                    Snake(result.Outcome),
                    result.Answer,
                    [.. result.Evidence.Select(evidence => new EvidenceResponse(
                        "code", evidence.Path, evidence.StartLine, evidence.EndLine, record.CommitSha,
                        evidence.Supports, evidence.Excerpt))],
                    result.Unknowns,
                    result.Limitations),
            investigation.ErrorCode);
    }

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
