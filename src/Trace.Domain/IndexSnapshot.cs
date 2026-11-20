using System.Text.RegularExpressions;

namespace Trace.Domain;

public enum SnapshotStatus
{
    Building,
    Completed,
    Failed,
}

/// <summary>
/// The index of one repository at one full commit SHA. Chunks are written while the snapshot is
/// <see cref="SnapshotStatus.Building"/>; once <see cref="SnapshotStatus.Completed"/> the snapshot
/// and its chunks are immutable, which is what makes citations stable.
/// </summary>
public sealed partial class IndexSnapshot : Entity
{
    public const int MaxVersionLength = 100;

    private IndexSnapshot()
    {
    }

    public Guid RepositoryId { get; private set; }

    public string CommitSha { get; private set; } = null!;

    public string Branch { get; private set; } = null!;

    public string ParserVersion { get; private set; } = null!;

    public string EmbeddingModel { get; private set; } = null!;

    public SnapshotStatus Status { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static IndexSnapshot Start(
        Guid repositoryId,
        string commitSha,
        string branch,
        string parserVersion,
        string embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(commitSha);
        if (!FullCommitSha().IsMatch(commitSha))
        {
            throw new ArgumentException(
                "Must be a full 40-character lowercase hexadecimal commit SHA.", nameof(commitSha));
        }

        return new IndexSnapshot
        {
            RepositoryId = repositoryId,
            CommitSha = commitSha,
            Branch = Guard.Required(branch, Repository.MaxBranchLength, nameof(branch)),
            ParserVersion = Guard.Required(parserVersion, MaxVersionLength, nameof(parserVersion)),
            EmbeddingModel = Guard.Required(embeddingModel, MaxVersionLength, nameof(embeddingModel)),
            Status = SnapshotStatus.Building,
        };
    }

    public void Complete(DateTimeOffset completedAt)
    {
        EnsureBuilding();
        Status = SnapshotStatus.Completed;
        CompletedAt = completedAt.ToUniversalTime();
    }

    public void Fail()
    {
        EnsureBuilding();
        Status = SnapshotStatus.Failed;
    }

    private void EnsureBuilding()
    {
        if (Status != SnapshotStatus.Building)
        {
            throw new InvalidOperationException($"Snapshot is {Status}; only a building snapshot can change state.");
        }
    }

    // \z, not $: in .NET, $ also matches before a trailing newline.
    [GeneratedRegex(@"^[0-9a-f]{40}\z")]
    private static partial Regex FullCommitSha();
}
