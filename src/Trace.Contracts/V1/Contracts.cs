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

/// <param name="State"><c>queued</c>, <c>running</c>, <c>completed</c>, <c>failed</c> or <c>cancelled</c>.</param>
/// <param name="Result">Present only when <paramref name="State"/> is <c>completed</c>.</param>
/// <param name="ErrorCode">Present only when <paramref name="State"/> is <c>failed</c>.</param>
public sealed record InvestigationResponse(
    Guid InvestigationId,
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
    string? ErrorCode);

public sealed record InvestigationEventResponse(
    int Seq,
    string EventType,
    string? ToolName,
    string Summary,
    DateTimeOffset OccurredAt);
