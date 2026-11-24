using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Application.Issues;
using Trace.Application.Jobs;
using Trace.Contracts.V1;
using Trace.Domain;

namespace Trace.Api;

/// <summary>
/// <c>/api/v1</c>. Every endpoint sees only what the caller may see: the public demo repository
/// for everyone, and GitHub repositories only for a signed-in user with access. Anything else
/// answers 404, so its existence is not revealed.
/// </summary>
internal static partial class V1Endpoints
{
    public const string WriteRateLimit = "writes";

    private const string QuestionType = "question";
    private const string PlanType = "issue_to_plan";

    public static void MapV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/repositories", ListRepositoriesAsync);
        v1.MapPost("/investigations", CreateInvestigationAsync)
            .RequireRateLimiting(WriteRateLimit).RequireSignIn().RememberAnonymousVisitor();
        v1.MapGet("/investigations", ListInvestigationsAsync);
        v1.MapGet("/investigations/{id:guid}", GetInvestigationAsync).WithName(nameof(GetInvestigationAsync));
        v1.MapGet("/investigations/{id:guid}/events", GetInvestigationEventsAsync);
        v1.MapPost("/repositories/{id:guid}/index-jobs", CreateIndexJobAsync).RequireRateLimiting(WriteRateLimit).RequireSignIn();
        v1.MapGet("/repositories/{id:guid}/issues", ListIssuesAsync);
        v1.MapGet("/repositories/{id:guid}/issues/{number:int:min(1)}", GetIssueAsync);
        v1.MapGet("/jobs/{id:guid}", GetJobAsync).WithName(nameof(GetJobAsync));
        v1.MapPost("/jobs/{id:guid}/cancel", CancelJobAsync).RequireSignIn();
        MapPlanEndpoints(v1);
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
            // Only queues the work: the response is 202 and the client polls the investigation.
            var record = await investigator.CreateAsync(
                new AskQuestion(request.RepositoryId, request.SnapshotId, request.Question!.Trim(), request.IdempotencyKey),
                cancellationToken);

            return TypedResults.AcceptedAtRoute(
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

    /// <summary>
    /// The caller's own investigations, newest first, without answers, evidence or plans
    /// (ADR-020). A signed-in user is listed what that user asked; an anonymous caller what was
    /// asked from the same browser, or nothing. Paged like the issue list: <c>limit</c> (1 to
    /// 100, default 30) and the <c>cursor</c> of the previous page. <c>repositoryId</c> and
    /// <c>type</c> (<c>question</c> or <c>issue_to_plan</c>) narrow the list; a repository the
    /// caller cannot see answers 404, as everywhere else.
    /// </summary>
    private static async Task<IResult> ListInvestigationsAsync(
        Guid? repositoryId,
        string? type,
        int? limit,
        string? cursor,
        InvestigationHistory history,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (limit is < 1 or > InvestigationHistory.MaxPageSize)
        {
            errors["limit"] = [$"limit must be 1 to {InvestigationHistory.MaxPageSize}."];
        }

        InvestigationType? wanted = type switch
        {
            null => null,
            QuestionType => InvestigationType.Question,
            PlanType => InvestigationType.IssueToPlan,
            _ => null,
        };
        if (type is not null && wanted is null)
        {
            errors["type"] = [$"type must be '{QuestionType}' or '{PlanType}'."];
        }

        if (repositoryId == Guid.Empty)
        {
            errors["repositoryId"] = ["repositoryId must be a repository's ID."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        InvestigationCursor? after = null;
        if (!string.IsNullOrEmpty(cursor) && (after = InvestigationCursor.Parse(cursor)) is null)
        {
            return TypedResults.Problem(
                detail: "The cursor is not one this API returned.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_cursor" });
        }

        var page = await history.ListAsync(
            new InvestigationHistoryQuery(repositoryId, wanted, limit ?? InvestigationHistory.DefaultPageSize, after),
            cancellationToken);
        if (page is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);
        }

        // The list is one caller's own: nothing between the API and the browser may keep it.
        response.Headers.CacheControl = "no-store";
        return TypedResults.Ok(new InvestigationListResponse(
            [.. page.Items.Select(item => new InvestigationSummaryResponse(
                item.Id,
                Snake(item.Type),
                new InvestigationRepositoryResponse(item.RepositoryId, item.RepositoryOwner, item.RepositoryName),
                Snake(item.State),
                item.Outcome,
                item.CreatedAt,
                item.Question,
                item.IssueNumber is { } number ? new PlanIssueResponse(number, item.IssueTitle) : null,
                item.PlanId))],
            page.Next?.Encode()));
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

    private static async Task<IResult> CreateIndexJobAsync(
        Guid id,
        HttpRequest request,
        JobService jobs,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = request.Headers["Idempotency-Key"].ToString();
        if (idempotencyKey.Length > Investigation.MaxIdempotencyKeyLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = [$"Idempotency-Key must be at most {Investigation.MaxIdempotencyKeyLength} characters."],
            });
        }

        try
        {
            var job = await jobs.EnqueueIndexAsync(
                id, idempotencyKey.Length == 0 ? null : idempotencyKey, cancellationToken);
            return TypedResults.AcceptedAtRoute(ToResponse(job), nameof(GetJobAsync), new { id = job.Id });
        }
        catch (JobRequestException exception)
        {
            return TypedResults.Problem(
                detail: exception.Message,
                statusCode: exception.Error == JobRequestError.RepositoryNotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetJobAsync(Guid id, JobService jobs, CancellationToken cancellationToken) =>
        await jobs.FindAsync(id, cancellationToken) is { } job
            ? TypedResults.Ok(ToResponse(job))
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    /// <summary>Asks for cancellation. Idempotent: cancelling a finished job returns it unchanged.</summary>
    private static async Task<IResult> CancelJobAsync(Guid id, JobService jobs, CancellationToken cancellationToken) =>
        await jobs.CancelAsync(id, cancellationToken) is { } job
            ? TypedResults.Accepted((string?)null, ToResponse(job))
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Open issues of a repository the caller can see, newest first, without bodies. Paged by
    /// cursor: <c>limit</c> (1 to 100, default 30) and the <c>cursor</c> of the previous page.
    /// </summary>
    private static async Task<IResult> ListIssuesAsync(
        Guid id,
        int? limit,
        string? cursor,
        IssueReader issues,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > IssueLimits.MaxPageSize)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = [$"limit must be 1 to {IssueLimits.MaxPageSize}."],
            });
        }

        try
        {
            var page = await issues.ListOpenAsync(id, limit ?? IssueLimits.DefaultPageSize, cursor, cancellationToken);
            return TypedResults.Ok(new IssueListResponse([.. page.Items.Select(ToResponse)], page.NextCursor));
        }
        catch (IssueReadException exception)
        {
            return IssueProblem(exception, id, loggers);
        }
    }

    /// <summary>One issue with its body and first comments, cut to the size limits and flagged where cut.</summary>
    private static async Task<IResult> GetIssueAsync(
        Guid id,
        int number,
        IssueReader issues,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        try
        {
            var issue = await issues.GetAsync(id, number, cancellationToken);
            var summary = issue.Summary;
            return TypedResults.Ok(new IssueResponse(
                summary.Number,
                summary.Title,
                summary.State,
                summary.Author,
                summary.Labels,
                issue.Body,
                issue.BodyTruncated,
                summary.CommentCount,
                [.. issue.Comments.Select(c => new IssueCommentResponse(c.Id, c.Author, c.Body, c.BodyTruncated, c.CreatedAt))],
                issue.CommentsTruncated,
                summary.CreatedAt,
                summary.UpdatedAt));
        }
        catch (IssueReadException exception)
        {
            return IssueProblem(exception, id, loggers);
        }
    }

    /// <summary>
    /// Every way reading issues can fail has its own status and a fixed <c>code</c>. A repository
    /// the caller cannot see is a plain 404, the same as everywhere else. The log line carries
    /// the repository ID and the code only.
    /// </summary>
    private static IResult IssueProblem(IssueReadException exception, Guid repositoryId, ILoggerFactory loggers)
    {
        var (status, detail) = exception.Error switch
        {
            IssueReadError.RepositoryNotFound => (StatusCodes.Status404NotFound, (string?)null),
            IssueReadError.IssueNotFound => (StatusCodes.Status404NotFound, "This repository has no issue with that number. Pull requests are not issues."),
            IssueReadError.InvalidCursor => (StatusCodes.Status400BadRequest, "The cursor is not one this API returned."),
            IssueReadError.IssuesPermissionMissing => (
                StatusCodes.Status409Conflict,
                "The GitHub App installation for this repository has not granted read access to issues. Its owner must accept the app's updated permissions on GitHub."),
            IssueReadError.IssuesDisabled => (StatusCodes.Status409Conflict, "Issues are switched off for this repository on GitHub."),
            IssueReadError.IssuesUnavailable => (StatusCodes.Status409Conflict, "This repository has no source of issues."),
            IssueReadError.GitHubRateLimited => (StatusCodes.Status503ServiceUnavailable, "GitHub is limiting requests right now. Try again later."),
            _ => (StatusCodes.Status502BadGateway, "GitHub did not return the issues. Try again."),
        };

        if (exception.Error == IssueReadError.RepositoryNotFound)
        {
            return TypedResults.Problem(statusCode: status);
        }

        if (status >= StatusCodes.Status409Conflict)
        {
            IssueReadFailed(loggers.CreateLogger(typeof(V1Endpoints).FullName!), repositoryId, exception.Code);
        }

        return TypedResults.Problem(
            detail: detail,
            statusCode: status,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Issues of repository {RepositoryId} could not be read: {Code}")]
    private static partial void IssueReadFailed(ILogger logger, Guid repositoryId, string code);

    private static IssueSummaryResponse ToResponse(IssueSummary issue) =>
        new(issue.Number, issue.Title, issue.State, issue.Author, issue.Labels, issue.CommentCount, issue.CreatedAt, issue.UpdatedAt);

    private static JobResponse ToResponse(Job job) =>
        new(
            job.Id,
            job.Kind,
            Snake(job.Status),
            job.Attempts,
            job.MaxAttempts,
            job.RequestedCancelAt is not null,
            job.ErrorCode,
            job.CreatedAt,
            job.UpdatedAt);

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
            record.JobId,
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
            investigation.ErrorCode,
            new UsageResponse(investigation.InputTokens, investigation.OutputTokens, investigation.CacheReadTokens, investigation.CacheWriteTokens));
    }

    private static string Snake<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
