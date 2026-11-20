namespace Trace.Domain;

public enum EvidenceSourceKind
{
    Code,
    Issue,
    Commit,
}

/// <summary>A piece of evidence an investigation relies on, scoped to the investigation's snapshot.</summary>
public sealed class EvidenceRef : Entity
{
    private EvidenceRef()
    {
    }

    public Guid InvestigationId { get; private set; }

    /// <summary>Always the investigation's pinned snapshot; lets the database reject cross-snapshot citations.</summary>
    public Guid SnapshotId { get; private set; }

    public Guid? ChunkId { get; private set; }

    public EvidenceSourceKind SourceKind { get; private set; }

    /// <summary>Exact location of the evidence (JSON), e.g. path and line range for code.</summary>
    public string LocatorJson { get; private set; } = null!;

    /// <summary>Lowercase hex SHA-256 of the cited excerpt.</summary>
    public string ExcerptHash { get; private set; } = null!;

    public static EvidenceRef ForCode(Investigation investigation, SourceChunk chunk, string locatorJson, string excerpt)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentException.ThrowIfNullOrWhiteSpace(locatorJson);
        ArgumentException.ThrowIfNullOrEmpty(excerpt);
        if (chunk.SnapshotId != investigation.SnapshotId)
        {
            throw new InvalidOperationException("Evidence must come from the investigation's pinned snapshot.");
        }

        return ForCode(investigation, chunk.Id, locatorJson, excerpt);
    }

    /// <summary>
    /// For a chunk known only by ID. That the chunk belongs to the investigation's snapshot is
    /// then enforced by the database's composite foreign key rather than here.
    /// </summary>
    public static EvidenceRef ForCode(Investigation investigation, Guid chunkId, string locatorJson, string excerpt)
    {
        ArgumentNullException.ThrowIfNull(investigation);
        ArgumentException.ThrowIfNullOrWhiteSpace(locatorJson);
        ArgumentException.ThrowIfNullOrEmpty(excerpt);

        return new EvidenceRef
        {
            InvestigationId = investigation.Id,
            SnapshotId = investigation.SnapshotId,
            ChunkId = chunkId,
            SourceKind = EvidenceSourceKind.Code,
            LocatorJson = locatorJson,
            ExcerptHash = SourceChunk.ComputeContentHash(excerpt),
        };
    }
}
