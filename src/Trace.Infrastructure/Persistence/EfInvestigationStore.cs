using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Application.Investigations;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

internal sealed class EfInvestigationStore(TraceDbContext db) : IInvestigationStore
{
    public Task<Investigation?> FindByIdempotencyKeyAsync(
        Guid repositoryId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        db.Investigations.AsNoTracking().SingleOrDefaultAsync(
            i => i.RepositoryId == repositoryId && i.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> TryAddAsync(Investigation investigation, CancellationToken cancellationToken = default)
    {
        db.Investigations.Add(investigation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TraceDbContext.InvestigationIdempotencyIndex,
        })
        {
            db.Entry(investigation).State = EntityState.Detached;
            return false;
        }
    }

    public Task AppendEventAsync(InvestigationEvent investigationEvent, CancellationToken cancellationToken = default)
    {
        db.InvestigationEvents.Add(investigationEvent);
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task SaveAsync(
        Investigation investigation,
        IReadOnlyList<EvidenceRef> evidence,
        CancellationToken cancellationToken = default)
    {
        // One SaveChanges is one transaction: the state change and its evidence land together.
        db.EvidenceRefs.AddRange(evidence);
        return db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvestigationRecord?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default)
    {
        var found = await (
            from investigation in db.Investigations.AsNoTracking()
            join snapshot in db.IndexSnapshots on investigation.SnapshotId equals snapshot.Id
            join repository in db.Repositories on investigation.RepositoryId equals repository.Id
            where investigation.Id == investigationId && repository.Visibility == RepositoryVisibility.Public
            select new { investigation, snapshot.CommitSha }).SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var events = await db.InvestigationEvents.AsNoTracking()
            .Where(e => e.InvestigationId == investigationId)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken);

        return new InvestigationRecord(found.investigation, found.CommitSha, events);
    }
}

internal sealed class EfRepositoryCatalog(TraceDbContext db) : IRepositoryCatalog
{
    public const int MaxListed = 100;

    public async Task<IReadOnlyList<RepositorySummary>> ListVisibleAsync(CancellationToken cancellationToken = default)
    {
        var repositories = await db.Repositories.AsNoTracking()
            .Where(r => r.Visibility == RepositoryVisibility.Public)
            .OrderBy(r => r.Owner).ThenBy(r => r.Name).ThenBy(r => r.Id)
            .Take(MaxListed)
            .ToListAsync(cancellationToken);

        var ids = repositories.Select(r => r.Id).ToList();
        var completed = await db.IndexSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.RepositoryId) && s.Status == SnapshotStatus.Completed)
            .ToListAsync(cancellationToken);
        var latest = completed
            .GroupBy(s => s.RepositoryId)
            .ToDictionary(group => group.Key, group => group.MaxBy(s => s.CompletedAt)!);

        return [.. repositories.Select(r => new RepositorySummary(
            r.Id,
            r.Owner,
            r.Name,
            r.DefaultBranch,
            latest.TryGetValue(r.Id, out var snapshot)
                ? new SnapshotSummary(snapshot.Id, snapshot.CommitSha, snapshot.Branch, snapshot.CompletedAt!.Value)
                : null))];
    }

    public Task<bool> IsVisibleAsync(Guid repositoryId, CancellationToken cancellationToken = default) =>
        db.Repositories.AnyAsync(
            r => r.Id == repositoryId && r.Visibility == RepositoryVisibility.Public, cancellationToken);
}
