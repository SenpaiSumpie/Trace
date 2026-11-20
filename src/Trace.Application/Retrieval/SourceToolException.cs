using Trace.Domain;

namespace Trace.Application.Retrieval;

public enum SourceToolError
{
    InvalidArgument,
    SnapshotNotFound,
    SnapshotNotReady,
    EmbeddingModelMismatch,
    PathNotFound,
    RangeOutOfBounds,
}

/// <summary>A read-only source tool rejected its input. The message is safe to show to the caller.</summary>
public sealed class SourceToolException(SourceToolError error, string message) : Exception(message)
{
    public SourceToolError Error { get; } = error;
}

internal static class SnapshotAccess
{
    /// <summary>
    /// Resolves a snapshot for reading. A snapshot of another repository is indistinguishable
    /// from one that does not exist, and only completed snapshots are readable.
    /// </summary>
    public static async Task<IndexSnapshot> RequireCompletedAsync(
        ISnapshotReader reader,
        Guid repositoryId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        var snapshot = await reader.FindSnapshotAsync(repositoryId, snapshotId, cancellationToken)
            ?? throw new SourceToolException(
                SourceToolError.SnapshotNotFound, "The snapshot does not exist in this repository.");

        return snapshot.Status == SnapshotStatus.Completed
            ? snapshot
            : throw new SourceToolException(
                SourceToolError.SnapshotNotReady, "The snapshot is not a completed index and cannot be read.");
    }
}
