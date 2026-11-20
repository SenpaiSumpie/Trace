using System.Text.Json;
using Trace.Application.Jobs;
using Trace.Domain;

namespace Trace.Application.Indexing;

/// <summary>Where a repository's content can be read from, or null if it has no source configured.</summary>
public interface IRepositorySourceResolver
{
    Task<IRepositorySource?> ResolveAsync(Guid repositoryId, CancellationToken cancellationToken = default);
}

/// <summary>Indexes a repository's current content on a worker.</summary>
public sealed class IndexJobHandler(
    SnapshotIndexer indexer,
    ISnapshotStore store,
    IRepositorySourceResolver sources) : IJobHandler
{
    public const string JobKind = "repository.index";
    public const string NoSourceError = "no_source";
    public const string SourceLimitError = "source_limit_exceeded";

    public string Kind => JobKind;

    public static string Payload(Guid repositoryId) =>
        JsonSerializer.Serialize(new IndexJobPayload(repositoryId), JsonSerializerOptions.Web);

    public async Task HandleAsync(JobContext context)
    {
        var repositoryId = RepositoryId(context.PayloadJson);
        var source = await sources.ResolveAsync(repositoryId, context.CancellationToken)
            ?? throw new JobFailedException(NoSourceError, "The repository has no source to index.");

        // An earlier attempt of this job, or a job whose worker died, may have left a build
        // behind; it would otherwise block this revision from ever being indexed.
        await store.FailAbandonedBuildsAsync(repositoryId, context.JobId, context.CancellationToken);

        try
        {
            await indexer.IndexAsync(repositoryId, source, context.CancellationToken, context.JobId);
        }
        catch (SourceLimitExceededException exception)
        {
            throw new JobFailedException(SourceLimitError, exception.Message, exception);
        }
    }

    public Task OnAbandonedAsync(
        Guid jobId,
        string payloadJson,
        JobStatus finalStatus,
        string? errorCode,
        CancellationToken cancellationToken) =>
        store.FailAbandonedBuildsAsync(RepositoryId(payloadJson), jobId, cancellationToken);

    private static Guid RepositoryId(string payloadJson)
    {
        try
        {
            return JsonSerializer.Deserialize<IndexJobPayload>(payloadJson, JsonSerializerOptions.Web)!.RepositoryId;
        }
        catch (Exception exception) when (exception is JsonException or NullReferenceException)
        {
            throw new JobFailedException(JobErrors.InvalidPayload, "The job payload is not a valid repository reference.", exception);
        }
    }

    private sealed record IndexJobPayload(Guid RepositoryId);
}
