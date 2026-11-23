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
/// <param name="InputTokens">Every input token, including the two cached kinds.</param>
/// <param name="CacheReadTokens">Input tokens read from the provider's prompt cache (billed at a fraction of the normal price).</param>
/// <param name="CacheWriteTokens">Input tokens written to the provider's prompt cache (billed somewhat above the normal price).</param>
public sealed record UsageResponse(long InputTokens, long OutputTokens, long CacheReadTokens = 0, long CacheWriteTokens = 0);

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

/// <summary>An issue as it appears in a list: no body. Every text field is untrusted content.</summary>
/// <param name="State"><c>open</c> or <c>closed</c>.</param>
/// <param name="Author">The login of whoever opened it, when known.</param>
public sealed record IssueSummaryResponse(
    int Number,
    string Title,
    string State,
    string? Author,
    IReadOnlyList<string> Labels,
    int CommentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One page of a list. Pass <paramref name="NextCursor"/> back as <c>cursor</c> to get the
/// next page; null means there is none. A page may hold fewer items than the limit asked for.
/// </summary>
public sealed record IssueListResponse(IReadOnlyList<IssueSummaryResponse> Items, string? NextCursor);

/// <param name="BodyTruncated">The comment was longer than the limit and has been cut.</param>
public sealed record IssueCommentResponse(long Id, string? Author, string Body, bool BodyTruncated, DateTimeOffset CreatedAt);

/// <summary>One issue with its first comments, oldest first. Every text field is untrusted content.</summary>
/// <param name="BodyTruncated">The body was longer than the limit and has been cut.</param>
/// <param name="CommentCount">How many comments the issue has, which can be more than are returned.</param>
/// <param name="CommentsTruncated">True when the issue has comments that are not in <paramref name="Comments"/>.</param>
public sealed record IssueResponse(
    int Number,
    string Title,
    string State,
    string? Author,
    IReadOnlyList<string> Labels,
    string Body,
    bool BodyTruncated,
    int CommentCount,
    IReadOnlyList<IssueCommentResponse> Comments,
    bool CommentsTruncated,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record UserResponse(string Login);

/// <param name="User">The signed-in user, or null when anonymous.</param>
/// <param name="SignInAvailable">False when no GitHub App is configured.</param>
/// <param name="CanStartWork">Whether this caller may ask questions and queue jobs.</param>
public sealed record MeResponse(UserResponse? User, bool SignInAvailable, bool CanStartWork);
