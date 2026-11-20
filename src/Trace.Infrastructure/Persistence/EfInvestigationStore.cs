using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Application.Access;
using Trace.Application.Investigations;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

internal sealed class EfInvestigationStore(TraceDbContext db, ICurrentUser currentUser) : IInvestigationStore
{
    public Task<Investigation?> FindByIdempotencyKeyAsync(
        Guid repositoryId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        db.Investigations.AsNoTracking().SingleOrDefaultAsync(
            i => i.RepositoryId == repositoryId && i.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<bool> TryAddAsync(Investigation investigation, Job job, CancellationToken cancellationToken = default)
    {
        // One SaveChanges is one transaction: an investigation never exists without its job.
        db.Investigations.Add(investigation);
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(job).State = EntityState.Detached;
            db.Entry(investigation).State = EntityState.Detached;
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: TraceDbContext.InvestigationIdempotencyIndex,
        })
        {
            db.Entry(investigation).State = EntityState.Detached;
            db.Entry(job).State = EntityState.Detached;
            return false;
        }
    }

    public Task<Investigation?> FindForUpdateAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
        db.Investigations.SingleOrDefaultAsync(i => i.Id == investigationId, cancellationToken);

    public async Task<int> GetLastEventSeqAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
        await db.InvestigationEvents
            .Where(e => e.InvestigationId == investigationId)
            .MaxAsync(e => (int?)e.Seq, cancellationToken) ?? 0;

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

    public async Task<long> SumTokensSinceAsync(DateTimeOffset since, CancellationToken cancellationToken = default) =>
        await db.Investigations
            .Where(i => i.CreatedAt >= since)
            .SumAsync(i => (long?)(i.InputTokens + i.OutputTokens), cancellationToken) ?? 0;

    public async Task<InvestigationRecord?> GetAsync(Guid investigationId, CancellationToken cancellationToken = default)
    {
        var found = await (
            from investigation in db.Investigations.AsNoTracking()
            join snapshot in db.IndexSnapshots on investigation.SnapshotId equals snapshot.Id
            join repository in db.VisibleRepositories(currentUser.UserId) on investigation.RepositoryId equals repository.Id
            where investigation.Id == investigationId
            select new { investigation, snapshot.CommitSha }).SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var events = await db.InvestigationEvents.AsNoTracking()
            .Where(e => e.InvestigationId == investigationId)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken);

        var dedupeKey = investigationId.ToString();
        var jobId = await db.Jobs
            .Where(j => j.Kind == InvestigationJobHandler.JobKind && j.DedupeKey == dedupeKey)
            .Select(j => (Guid?)j.Id)
            .SingleOrDefaultAsync(cancellationToken);

        return new InvestigationRecord(found.investigation, found.CommitSha, events, jobId);
    }
}

internal sealed class EfRepositoryCatalog(TraceDbContext db, ICurrentUser currentUser) : IRepositoryCatalog
{
    public const int MaxListed = 100;

    public async Task<IReadOnlyList<RepositorySummary>> ListVisibleAsync(CancellationToken cancellationToken = default)
    {
        var repositories = await db.VisibleRepositories(currentUser.UserId).AsNoTracking()
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
        db.VisibleRepositories(currentUser.UserId).AnyAsync(r => r.Id == repositoryId, cancellationToken);
}

internal static class RepositoryVisibilityQueries
{
    /// <summary>
    /// The one definition of what a caller may see. Everyone sees the public demo fixture (public,
    /// not from GitHub). A GitHub repository, public or private, is visible only to a signed-in
    /// user holding an access row for it, and only while its installation is active.
    /// </summary>
    public static IQueryable<Repository> VisibleRepositories(this TraceDbContext db, Guid? userId) =>
        db.Repositories.Where(repository =>
            (repository.InstallationId == null && repository.Visibility == RepositoryVisibility.Public)
            || (userId != null
                && db.RepositoryAccess.Any(access => access.UserId == userId && access.RepositoryId == repository.Id)
                && db.GitHubInstallations.Any(installation =>
                    installation.Id == repository.InstallationId && installation.Status == InstallationStatus.Active)));
}
