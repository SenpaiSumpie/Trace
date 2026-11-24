using Microsoft.EntityFrameworkCore;
using Trace.Application.Access;
using Trace.Application.Investigations;
using Trace.Domain;

namespace Trace.Infrastructure.Persistence;

/// <summary>
/// One query per page. The owner condition and the repository visibility condition are in the
/// query itself, so a row the caller may not be listed never leaves the database and is never
/// counted: a page is cut after the caller's own rows, and whether a further page exists is
/// decided by looking for one more of them.
/// <para>
/// The stored result is not loaded: only its <c>outcome</c> members are read, by the database.
/// The two partial indexes of the <c>InvestigationHistory</c> migration match the order.
/// </para>
/// </summary>
internal sealed class EfInvestigationHistoryStore(TraceDbContext db, ICurrentUser currentUser) : IInvestigationHistoryStore
{
    public async Task<InvestigationHistoryPage> ListAsync(InvestigationHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Whose rows. A signed-in user: the ones that user asked for. Anyone else: the ones
        // asked from this browser. A row with neither (from before history existed, or whose
        // user was deleted) belongs to nobody's list.
        IQueryable<Investigation> own;
        if (currentUser.UserId is { } userId)
        {
            own = db.Investigations.Where(investigation => investigation.UserId == userId);
        }
        else if (currentUser.VisitorKey is { } visitorKey && Investigation.IsVisitorKey(visitorKey))
        {
            own = db.Investigations.Where(investigation => investigation.UserId == null && investigation.VisitorKey == visitorKey);
        }
        else
        {
            return new InvestigationHistoryPage([], null);
        }

        if (query.RepositoryId is { } repositoryId)
        {
            own = own.Where(investigation => investigation.RepositoryId == repositoryId);
        }

        if (query.Type is { } type)
        {
            own = own.Where(investigation => investigation.Type == type);
        }

        if (query.After is { } after)
        {
            var afterCreatedAt = after.CreatedAt.ToUniversalTime();
            var afterId = after.Id;
            own = own.Where(investigation => EF.Functions.LessThan(
                ValueTuple.Create(investigation.CreatedAt, investigation.Id),
                ValueTuple.Create(afterCreatedAt, afterId)));
        }

        var rows = await (
            from investigation in own.AsNoTracking()
            join repository in db.VisibleRepositories(currentUser.UserId) on investigation.RepositoryId equals repository.Id
            join plan in db.Plans on investigation.Id equals plan.InvestigationId into plans
            from plan in plans.DefaultIfEmpty()
            orderby investigation.CreatedAt descending, investigation.Id descending
            select new
            {
                investigation.Id,
                investigation.Type,
                investigation.State,
                investigation.CreatedAt,
                investigation.Question,
                RepositoryId = repository.Id,
                repository.Owner,
                repository.Name,
                PlanId = (Guid?)plan!.Id,
                IssueNumber = (int?)plan.IssueNumber,
                plan.IssueTitle,
                Outcome = TraceDbContext.JsonText(investigation.ResultJson, "outcome"),
                PlanOutcome = TraceDbContext.JsonText(investigation.ResultJson, "plan", "outcome"),
            })
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);

        var items = rows.Take(query.Limit).Select(row => new InvestigationHistoryItem(
            row.Id,
            row.Type,
            row.State,
            row.State != InvestigationState.Completed ? null : Outcome(row.Type, row.Outcome, row.PlanOutcome),
            row.CreatedAt,
            row.RepositoryId,
            row.Owner,
            row.Name,
            row.Type == InvestigationType.Question ? row.Question : null,
            row.PlanId,
            row.IssueNumber,
            row.IssueTitle)).ToList();

        return new InvestigationHistoryPage(
            items,
            rows.Count > query.Limit ? new InvestigationCursor(items[^1].CreatedAt, items[^1].Id) : null);
    }

    /// <summary>Only the fixed names are passed on; anything else stored there is reported as no outcome.</summary>
    private static string? Outcome(InvestigationType type, string? outcome, string? planOutcome) =>
        (type == InvestigationType.IssueToPlan ? planOutcome : outcome) switch
        {
            "answered" => "answered",
            "planned" => "planned",
            "insufficient_evidence" => "insufficient_evidence",
            _ => null,
        };
}
