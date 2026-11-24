using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Trace.Application.Telemetry;
using Trace.Domain;

namespace Trace.Application.Investigations;

/// <summary>
/// A position in the history list: the creation time and ID of the last row of a page. It is
/// handed to clients as an opaque string and checked when it comes back.
/// <para>
/// A cursor is only ever a position. It carries no user, no filter and no permission: the
/// rules about whose rows are listed are applied by the query on every page, so a cursor made
/// up by a caller, or taken from another caller, selects a different place in the caller's own
/// rows and nothing else. That is why it is not signed.
/// </para>
/// </summary>
public sealed record InvestigationCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public const int MaxLength = 120;

    private const string Version = "h1";

    public string Encode() => Base64Url.EncodeToString(Encoding.ASCII.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"{Version}:{CreatedAt.UtcTicks}:{Id:N}")));

    /// <summary>The cursor, or null when the text is not one <see cref="Encode"/> wrote.</summary>
    public static InvestigationCursor? Parse(string? text)
    {
        if (text is not { Length: > 0 and <= MaxLength } || !Base64Url.IsValid(text))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(text));
        }
        catch (FormatException)
        {
            return null;
        }

        var parts = decoded.Split(':');
        if (parts.Length != 3
            || parts[0] != Version
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(parts[2], "N", out var id))
        {
            return null;
        }

        var cursor = new InvestigationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);

        // Only the exact text Encode writes is accepted, so one position has one spelling.
        return cursor.Encode() == text ? cursor : null;
    }
}

/// <param name="RepositoryId">Only investigations of this repository; null for every repository.</param>
/// <param name="Type">Only investigations of this type; null for both.</param>
/// <param name="Limit">How many rows to return at most.</param>
/// <param name="After">Only rows that sort after this position; null for the first page.</param>
public sealed record InvestigationHistoryQuery(Guid? RepositoryId, InvestigationType? Type, int Limit, InvestigationCursor? After);

/// <summary>
/// What a history row shows and no more. There is no answer, evidence or plan document here.
/// </summary>
/// <param name="Question">The question as asked; null for a plan investigation, whose stored question is a fixed sentence.</param>
/// <param name="Outcome"><c>answered</c>, <c>planned</c> or <c>insufficient_evidence</c> once completed; otherwise null.</param>
/// <param name="PlanId">The plan of an issue-to-plan investigation; null for a question.</param>
/// <param name="IssueTitle">The issue's title as the run read it: untrusted text, null until the run has read the issue.</param>
public sealed record InvestigationHistoryItem(
    Guid Id,
    InvestigationType Type,
    InvestigationState State,
    string? Outcome,
    DateTimeOffset CreatedAt,
    Guid RepositoryId,
    string RepositoryOwner,
    string RepositoryName,
    string? Question,
    Guid? PlanId,
    int? IssueNumber,
    string? IssueTitle);

/// <param name="Next">The position to continue from; null when the caller has no further row.</param>
public sealed record InvestigationHistoryPage(IReadOnlyList<InvestigationHistoryItem> Items, InvestigationCursor? Next);

/// <summary>
/// Reads the history of the current caller: rows the caller asked for, in repositories the
/// caller can see now. Both conditions are part of every query.
/// </summary>
public interface IInvestigationHistoryStore
{
    /// <summary>
    /// Up to <c>Limit</c> rows, newest first (creation time, then ID, both descending), for the
    /// current caller. Returns at most <c>Limit</c> items; <c>Next</c> is set only when a
    /// further row of the caller exists.
    /// </summary>
    Task<InvestigationHistoryPage> ListAsync(InvestigationHistoryQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// The caller's past investigations, newest first (ADR-020).
/// <para>
/// Who is listed what: a signed-in user gets the investigations that user asked for; an
/// anonymous caller gets those asked from the same browser (its visitor cookie) and nothing
/// when it has none. In both cases only while the repository is still visible to the caller.
/// Reading one investigation by its ID is a different rule and is not changed by this: anyone
/// who may see the repository may read it.
/// </para>
/// <para>
/// The span carries counts and flags only: never a question, a title or a repository name.
/// </para>
/// </summary>
public sealed class InvestigationHistory(IInvestigationHistoryStore store, IRepositoryCatalog repositories)
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;

    /// <summary>
    /// One page, or null when <paramref name="query"/> names a repository the caller cannot
    /// see, which is indistinguishable from one that does not exist.
    /// </summary>
    public async Task<InvestigationHistoryPage?> ListAsync(InvestigationHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaxPageSize);

        using var activity = TraceTelemetry.Source.StartActivity("investigations.list");
        activity?.SetTag("trace.history.filtered_by_repository", query.RepositoryId is not null);
        activity?.SetTag("trace.history.filtered_by_type", query.Type is not null);
        activity?.SetTag("trace.history.limit", query.Limit);

        if (query.RepositoryId is { } repositoryId && !await repositories.IsVisibleAsync(repositoryId, cancellationToken))
        {
            activity?.SetTag("trace.history.outcome", "repository_not_found");
            return null;
        }

        var page = await store.ListAsync(query, cancellationToken);
        activity?.SetTag("trace.history.outcome", "ok");
        activity?.SetTag("trace.history.count", page.Items.Count);
        activity?.SetTag("trace.history.has_more", page.Next is not null);
        return page;
    }
}
