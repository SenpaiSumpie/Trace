namespace Trace.Contracts.V1;

public sealed record SnapshotResponse(Guid Id, string CommitSha, string Branch, DateTimeOffset CompletedAt);

public sealed record RepositoryResponse(
    Guid Id,
    string Owner,
    string Name,
    string DefaultBranch,
    SnapshotResponse? LatestSnapshot);

/// <summary>Body of <c>POST /api/v1/investigations</c>.</summary>
/// <param name="Type">Only <c>question</c> is supported so far.</param>
/// <param name="IdempotencyKey">Optional. Repeating a request with the same key returns the first investigation.</param>
public sealed record CreateInvestigationRequest(
    Guid RepositoryId,
    Guid SnapshotId,
    string? Type,
    string? Question,
    string? IdempotencyKey);

public sealed record EvidenceResponse(
    string Source,
    string Path,
    int StartLine,
    int EndLine,
    string CommitSha,
    string Supports,
    string Excerpt);

/// <param name="Outcome"><c>answered</c> or <c>insufficient_evidence</c>.</param>
public sealed record InvestigationResultResponse(
    string Outcome,
    string Answer,
    IReadOnlyList<EvidenceResponse> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> Limitations);

/// <param name="State"><c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>. Poll until it is one of the last three.</param>
/// <param name="JobId">The background job running the investigation; use it to cancel.</param>
/// <param name="Result">Present only when <paramref name="State"/> is <c>completed</c>.</param>
/// <param name="ErrorCode">Present only when <paramref name="State"/> is <c>failed</c>.</param>
public sealed record InvestigationResponse(
    Guid InvestigationId,
    Guid? JobId,
    Guid RepositoryId,
    Guid SnapshotId,
    string CommitSha,
    string Type,
    string Question,
    string State,
    string? ModelId,
    string? PromptVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    InvestigationResultResponse? Result,
    string? ErrorCode,
    UsageResponse Usage);

/// <summary>Model tokens spent on the investigation so far, across all attempts.</summary>
public sealed record UsageResponse(long InputTokens, long OutputTokens);

public sealed record InvestigationEventResponse(
    int Seq,
    string EventType,
    string? ToolName,
    string Summary,
    DateTimeOffset OccurredAt);

/// <param name="Kind"><c>investigation.run</c> or <c>repository.index</c>.</param>
/// <param name="Status"><c>queued</c>, <c>running</c>, <c>succeeded</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="ErrorCode">Why the job failed, or the reason for its most recent retry.</param>
public sealed record JobResponse(
    Guid Id,
    string Kind,
    string Status,
    int Attempts,
    int MaxAttempts,
    bool CancelRequested,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserResponse(string Login);

/// <param name="User">The signed-in user, or null when anonymous.</param>
/// <param name="SignInAvailable">False when no GitHub App is configured.</param>
/// <param name="CanStartWork">Whether this caller may ask questions and queue jobs.</param>
public sealed record MeResponse(UserResponse? User, bool SignInAvailable, bool CanStartWork);
