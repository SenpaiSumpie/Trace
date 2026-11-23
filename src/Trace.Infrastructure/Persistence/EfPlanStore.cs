using Microsoft.EntityFrameworkCore;
using Npgsql;
using Trace.Application.Access;
using Trace.Application.Plans;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

/// <summary>
/// Shares the scope's <see cref="TraceDbContext"/> with <see cref="EfInvestigationStore"/>, so an
/// investigation found there and a plan found here are saved by the same
/// <c>SaveChanges</c>: one transaction.
/// </summary>
internal sealed class EfPlanStore(TraceDbContext db, ICurrentUser currentUser) : IPlanStore
{
    public async Task<bool> TryAddAsync(
        Investigation investigation,
        Plan plan,
        Job job,
        CancellationToken cancellationToken = default)
    {
        // One SaveChanges is one transaction: a plan investigation never exists without its plan and its job.
        db.Investigations.Add(investigation);
        db.Plans.Add(plan);
        db.Jobs.Add(job);
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
            return false;
        }
        finally
        {
            db.Entry(job).State = EntityState.Detached;
            db.Entry(plan).State = EntityState.Detached;
            db.Entry(investigation).State = EntityState.Detached;
        }
    }

    public Task<Plan?> FindForUpdateAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
        db.Plans.SingleOrDefaultAsync(plan => plan.InvestigationId == investigationId, cancellationToken);

    public Task SaveAsync(
        Investigation investigation,
        Plan plan,
        IReadOnlyList<EvidenceRef> evidence,
        CancellationToken cancellationToken = default)
    {
        if (db.Entry(investigation).State == EntityState.Detached || db.Entry(plan).State == EntityState.Detached)
        {
            throw new InvalidOperationException("The investigation and its plan must be loaded for update in this scope.");
        }

        // One SaveChanges is one transaction: the investigation's state, its evidence and the
        // plan's result land together, which is what the commit-time check of a plan requires.
        db.EvidenceRefs.AddRange(evidence);
        return db.SaveChangesAsync(cancellationToken);
    }

    public Task<Plan?> GetByInvestigationAsync(Guid investigationId, CancellationToken cancellationToken = default) =>
        (from plan in db.Plans.AsNoTracking()
         join repository in db.VisibleRepositories(currentUser.UserId) on plan.RepositoryId equals repository.Id
         where plan.InvestigationId == investigationId
         select plan).SingleOrDefaultAsync(cancellationToken);
}
