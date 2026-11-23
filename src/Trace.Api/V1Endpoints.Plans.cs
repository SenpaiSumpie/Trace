using System.Text;
using Microsoft.Net.Http.Headers;
using Trace.Application.Investigations;
using Trace.Application.Plans;
using Trace.Contracts.V1;
using Trace.Domain;

namespace Trace.Api;

/// <summary>
/// Plans over HTTP: ask for one, read it, mark it reviewed, export it as Markdown. The same
/// rules as the rest of <c>/api/v1</c>: a plan of a repository the caller cannot see answers a
/// plain 404; starting and reviewing need a signed-in user unless anonymous writes are on.
/// Nothing here can write to GitHub: the only thing any of it does outside TRACE's database is
/// read one issue (the plan's run, and the optional <c>checkIssue</c>).
/// </summary>
internal static partial class V1Endpoints
{
    private static void MapPlanEndpoints(RouteGroupBuilder v1)
    {
        v1.MapPost("/repositories/{id:guid}/issues/{number:int:min(1)}/plans", CreatePlanAsync)
            .RequireRateLimiting(WriteRateLimit).RequireSignIn();
        v1.MapGet("/plans/{id:guid}", GetPlanAsync).WithName(nameof(GetPlanAsync));
        v1.MapPost("/plans/{id:guid}/review", ReviewPlanAsync).RequireSignIn();
        v1.MapGet("/plans/{id:guid}/export", ExportPlanAsync);
    }

    /// <summary>
    /// Queues an issue-to-plan investigation and answers 202 with the plan, still unfinished.
    /// The client polls <c>GET /plans/{id}</c>, or the investigation and its events, exactly as
    /// for a question. Whether the issue exists is found out by the run, not here.
    /// </summary>
    private static async Task<IResult> CreatePlanAsync(
        Guid id,
        int number,
        CreatePlanRequest request,
        PlanInvestigator investigator,
        PlanService plans,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (request.SnapshotId == Guid.Empty)
        {
            errors["snapshotId"] = ["snapshotId is required."];
        }

        if (request.IdempotencyKey is { } key
            && (string.IsNullOrWhiteSpace(key) || key.Length > Investigation.MaxIdempotencyKeyLength))
        {
            errors["idempotencyKey"] = [$"idempotencyKey must be 1 to {Investigation.MaxIdempotencyKeyLength} characters when present."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        try
        {
            var record = await investigator.CreateAsync(
                new RequestPlan(id, request.SnapshotId, number, request.IdempotencyKey), cancellationToken);
            var plan = await plans.GetAsync(record.Plan.Id, cancellationToken)
                ?? throw new InvalidOperationException("The plan could not be read back.");

            return TypedResults.AcceptedAtRoute(ToResponse(plan, issueCheck: null), nameof(GetPlanAsync), new { id = plan.Plan.Id });
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

    /// <summary>
    /// The plan with its evidence. With <c>?checkIssue=true</c> the issue is read again and
    /// compared with what the plan was made from; that costs calls to GitHub, so it is never
    /// done unasked, and its failure is reported inside a successful response.
    /// </summary>
    private static async Task<IResult> GetPlanAsync(
        Guid id,
        bool? checkIssue,
        PlanService plans,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await plans.GetAsync(id, cancellationToken) is not { } plan)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
            }

            var check = checkIssue == true ? await plans.CheckIssueAsync(plan, cancellationToken) : null;
            return TypedResults.Ok(ToResponse(plan, check));
        }
        catch (PlanSchemaException)
        {
            return Unreadable(id, loggers);
        }
    }

    /// <summary>
    /// Acknowledges that a person has read the plan. Idempotent: the first time stands. It
    /// changes the plan's reviewed time in TRACE and nothing else, and calls nothing outside TRACE.
    /// </summary>
    private static async Task<IResult> ReviewPlanAsync(
        Guid id,
        PlanService plans,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var (outcome, plan) = await plans.ReviewAsync(id, cancellationToken);
            switch (outcome)
            {
                case PlanReviewOutcome.NotFound:
                    return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

                case PlanReviewOutcome.NotReviewable:
                    return TypedResults.Problem(
                        detail: plan!.State == PlanView.InsufficientEvidence
                            ? "This run found insufficient evidence and made no plan, so there is nothing to review."
                            : "Only a finished plan can be marked as reviewed.",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?> { ["code"] = "plan_not_reviewable", ["state"] = plan.State });

                default:
                    if (outcome == PlanReviewOutcome.Marked)
                    {
                        PlanReviewed(loggers.CreateLogger(typeof(V1Endpoints).FullName!), id);
                    }

                    return TypedResults.Ok(ToResponse(plan!, issueCheck: null));
            }
        }
        catch (PlanSchemaException)
        {
            return Unreadable(id, loggers);
        }
    }

    /// <summary>
    /// The plan as a Markdown file. The file name is built from the issue number and the plan's
    /// ID only, and the browser is told not to guess another content type.
    /// </summary>
    private static async Task<IResult> ExportPlanAsync(
        Guid id,
        HttpResponse response,
        PlanService plans,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var export = await plans.ExportAsync(id, cancellationToken);
            switch (export.Outcome)
            {
                case PlanExportOutcome.NotFound:
                    return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

                case PlanExportOutcome.NotFinished:
                    return TypedResults.Problem(
                        detail: "This plan has no document to export: it is unfinished, failed or cancelled.",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?> { ["code"] = "plan_not_finished" });

                default:
                    response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
                    response.Headers[HeaderNames.CacheControl] = "no-store";
                    response.Headers[HeaderNames.ContentDisposition] =
                        new ContentDispositionHeaderValue("attachment") { FileName = export.FileName }.ToString();
                    return TypedResults.Text(export.Markdown!, PlanMarkdown.ContentType, Encoding.UTF8);
            }
        }
        catch (PlanSchemaException)
        {
            return Unreadable(id, loggers);
        }
    }

    /// <summary>A stored plan of a schema version this build cannot read. Reported by its fixed code; nothing of the document is returned.</summary>
    private static IResult Unreadable(Guid planId, ILoggerFactory loggers)
    {
        PlanUnreadable(loggers.CreateLogger(typeof(V1Endpoints).FullName!), planId);
        return TypedResults.Problem(
            detail: "The stored plan is of a schema version this server cannot read.",
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["code"] = "plan_unreadable" });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Plan {PlanId} was marked as reviewed")]
    private static partial void PlanReviewed(ILogger logger, Guid planId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Plan {PlanId} has a stored document this build cannot read")]
    private static partial void PlanUnreadable(ILogger logger, Guid planId);

    private static PlanResponse ToResponse(PlanView view, PlanIssueCheck? issueCheck)
    {
        var plan = view.Plan;
        var record = view.Investigation;
        var investigation = record.Investigation;

        return new PlanResponse(
            plan.Id,
            investigation.Id,
            record.JobId,
            plan.RepositoryId,
            plan.SnapshotId,
            record.CommitSha,
            new PlanIssueResponse(plan.IssueNumber, plan.IssueTitle),
            view.State,
            investigation.ErrorCode,
            investigation.ModelId,
            investigation.PromptVersion,
            plan.CreatedAt,
            plan.UpdatedAt > investigation.UpdatedAt ? plan.UpdatedAt : investigation.UpdatedAt,
            new UsageResponse(investigation.InputTokens, investigation.OutputTokens, investigation.CacheReadTokens, investigation.CacheWriteTokens),
            view.Result is { } result
                ? new PlanDocumentResponse(
                    result.SchemaVersion,
                    ToResponse(result.Summary),
                    [.. result.Steps.Select(ToResponse)],
                    [.. result.FilesToChange.Select(file => new PlanFileResponse(file.Path, Snake(file.Change), file.Reason, file.Citations))],
                    [.. result.Risks.Select(ToResponse)],
                    [.. result.Verification.Select(ToResponse)],
                    [.. result.Unknowns.Select(ToResponse)],
                    [.. result.Evidence.Select(evidence => new PlanEvidenceResponse(
                        evidence.Id, "code", evidence.Path, evidence.StartLine, evidence.EndLine, record.CommitSha, evidence.Excerpt))])
                : null,
            view.Summary?.Plan.Trimmed is { } trimmed
                ? new PlanTrimmedResponse(
                    trimmed.Citations, trimmed.Steps, trimmed.Files, trimmed.Risks, trimmed.Verification, trimmed.Unknowns, trimmed.SummaryReplaced)
                : null,
            view.Summary?.Limitations ?? [],
            plan.ReviewedAt,
            issueCheck is null ? null : new PlanIssueCheckResponse(issueCheck.Status, issueCheck.Code));
    }

    private static PlanStatementResponse ToResponse(PlanStatement statement) => new(statement.Text, statement.Citations);
}
