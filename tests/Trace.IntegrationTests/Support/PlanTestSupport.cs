using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Application.Issues;

namespace Trace.IntegrationTests.Support;

/// <summary>
/// An issue source a test controls: it serves one issue, counts every fetch, and can fail the
/// way GitHub does. It stands in for both the resolver and the source.
/// </summary>
internal sealed class StubIssues : IIssueSourceResolver, IIssueSource
{
    private int fetches;

    /// <summary>The issue served; null means the repository has no such issue.</summary>
    public RawIssueThread? Thread { get; set; }

    /// <summary>When set, decides the outcome of a fetch from its number (1 for the first): an error to throw, or null to serve <see cref="Thread"/>.</summary>
    public Func<int, IssueReadError?> Failure { get; set; } = _ => null;

    /// <summary>False means the repository has nowhere to read issues from.</summary>
    public bool HasSource { get; set; } = true;

    public int Fetches => fetches;

    public List<Guid> ResolvedRepositories { get; } = [];

    public string Kind => "stub";

    public static RawIssueThread Issue(int number, string title, string body, params (long Id, string Body)[] comments) =>
        new(
            new RawIssue(
                number, title, "open", "zzauthor-marker", ["zzlabel-marker"], body, comments.Length,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            [.. comments.Select(comment => new RawIssueComment(comment.Id, "zzcommenter-marker", comment.Body, DateTimeOffset.UnixEpoch))]);

    public Task<IIssueSource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default)
    {
        ResolvedRepositories.Add(repositoryId);
        return Task.FromResult<IIssueSource?>(HasSource ? this : null);
    }

    public Task<RawIssuePage> ListOpenAsync(int limit, string? cursor, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("A plan run never lists issues.");

    public Task<RawIssueThread?> GetAsync(int number, int maxComments, CancellationToken cancellationToken = default)
    {
        if (Failure(Interlocked.Increment(ref fetches)) is { } error)
        {
            throw new IssueReadException(error);
        }

        return Task.FromResult(Thread is { } thread && thread.Issue.Number == number ? thread : null);
    }
}

/// <summary>Builds the final turn of a model that proposes a plan.</summary>
internal static class Proposal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static object Statement(string text, params (string Path, int StartLine, int EndLine)[] citations) =>
        new { text, citations = citations.Select(Location) };

    public static object File(string path, string change, string reason, params (string Path, int StartLine, int EndLine)[] citations) =>
        new { path, change, reason, citations = citations.Select(Location) };

    public static ModelTurn Turn(
        object? summary = null,
        object[]? steps = null,
        object[]? files = null,
        object[]? risks = null,
        object[]? verification = null,
        object[]? unknowns = null,
        bool sufficientEvidence = true) =>
        ModelTurn.Final(JsonSerializer.Serialize(
            new
            {
                sufficientEvidence,
                summary,
                steps = steps ?? [],
                filesToChange = files ?? [],
                risks = risks ?? [],
                verification = verification ?? [],
                unknowns = unknowns ?? [],
            },
            Json));

    private static object Location((string Path, int StartLine, int EndLine) citation) =>
        new { path = citation.Path, startLine = citation.StartLine, endLine = citation.EndLine };
}

/// <summary>A model call that never returns until it is cancelled.</summary>
internal sealed class BlockingChatModel(TaskCompletionSource called) : IChatModel
{
    public string ModelId => "blocking-v1";

    public async Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        called.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

/// <summary>Everything the application logs, at every level, as text.</summary>
internal sealed class CapturedLogs : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> lines = new();

    public IReadOnlyCollection<string> Lines => lines;

    /// <summary>Forgets what was logged so far (creating and indexing the test database, for example).</summary>
    public void Clear() => lines.Clear();

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        lines.Enqueue(state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? string.Join(' ', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
            : state.ToString() ?? string.Empty);
        return null;
    }

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        lines.Enqueue($"{formatter(state, exception)} {exception}");

    public void Dispose()
    {
    }
}
