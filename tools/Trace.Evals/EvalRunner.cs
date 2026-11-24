using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trace.Application.Investigations;
using Trace.Application.Jobs;
using Trace.Application.Retrieval;
using Trace.Domain;

namespace Trace.Evals;

/// <summary>The run cannot start: the snapshot is missing or is not the revision the dataset was labeled against.</summary>
public sealed class EvalException(string message) : Exception(message);

/// <param name="Split">Run only this split; null runs every question.</param>
/// <param name="QuestionIds">Run only the questions with these IDs; null applies no such limit.</param>
/// <param name="QuestionTimeout">A question still running after this long is stopped and recorded as failed with <c>timed_out</c>.</param>
public sealed record EvalRunRequest(
    EvalDataset Dataset,
    Guid RepositoryId,
    Guid SnapshotId,
    string ChatProvider,
    string EmbeddingProvider,
    ModelPrices? Prices,
    string TraceCommit,
    bool? TraceCommitDirty,
    TimeSpan QuestionTimeout,
    string? Split = null,
    bool AllowRevisionMismatch = false,
    IReadOnlySet<string>? QuestionIds = null);

/// <summary>
/// Runs labeled questions through the production pipeline and describes what happened.
/// <para>
/// It contains no investigation logic. Each question is created and run by the same
/// <see cref="QuestionInvestigator"/> the API and worker use, resolved from the same service
/// registration, as one final job attempt would run it; this class only reads back what the
/// pipeline stored. The repository and snapshot are fixed by the run, never by a question.
/// </para>
/// <para>
/// The one thing it does beside the pipeline is a search of its own per question, to measure
/// retrieval (ADR-010). That search is never part of an investigation.
/// </para>
/// </summary>
public sealed class EvalRunner(
    IServiceScopeFactory scopes,
    IChatModel model,
    TimeProvider timeProvider,
    ILogger<EvalRunner> logger)
{
    /// <summary>The investigation request was refused before anything ran (for example, the repository is not visible).</summary>
    public const string RequestRejectedError = "request_rejected";

    public const string InternalError = "internal_error";

    /// <summary>How many hits the retrieval measurement asks the search for: the 10 of "recall at 10".</summary>
    public const int RetrievalLimit = 10;

    /// <param name="onQuestion">Called after each question that was run, for progress output.</param>
    /// <param name="cancellationToken">Stops the run: the question in progress is cancelled and the rest are recorded as not run.</param>
    public async Task<EvalRunResult> RunAsync(
        EvalRunRequest request,
        Action<EvalQuestionResult>? onQuestion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.QuestionTimeout, TimeSpan.Zero);

        var snapshot = await FindSnapshotAsync(request);
        var revisionMatches = request.Dataset.Revision is { } revision ? revision == snapshot.CommitSha : (bool?)null;
        if (revisionMatches == false && !request.AllowRevisionMismatch)
        {
            throw new EvalException(
                $"The dataset was labeled against revision {request.Dataset.Revision} but the snapshot is {snapshot.CommitSha}. "
                + "Line labels may no longer point at the right lines.");
        }

        var questions = request.Dataset.Questions
            .Where(question => request.Split is null || question.Split == request.Split)
            .Where(question => request.QuestionIds is null || request.QuestionIds.Contains(question.Id))
            .ToList();

        var startedAt = timeProvider.GetUtcNow();
        var results = new List<EvalQuestionResult>(questions.Count);
        foreach (var question in questions)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Describe(question, EvalOutcomes.NotRun, fallbackError: null, record: null, request.Prices, TimeSpan.Zero));
                continue;
            }

            var result = await RunQuestionAsync(request, question, cancellationToken);
            results.Add(result);
            onQuestion?.Invoke(result);
        }

        var totalTokens = new EvalTokens(
            results.Sum(result => result.Tokens.Input),
            results.Sum(result => result.Tokens.Output),
            results.Sum(result => result.Tokens.CacheRead),
            results.Sum(result => result.Tokens.CacheWrite));

        return new EvalRunResult(
            EvalRunResult.CurrentSchemaVersion,
            RunId: Guid.NewGuid().ToString("N"),
            cancellationToken.IsCancellationRequested ? EvalRunStatus.Cancelled : EvalRunStatus.Completed,
            request.TraceCommit,
            request.TraceCommitDirty,
            new EvalDatasetInfo(
                request.Dataset.Name,
                request.Dataset.Version,
                request.Dataset.Revision,
                revisionMatches,
                request.QuestionIds is not null ? EvalDatasetInfo.SubsetSplit : request.Split ?? "all",
                questions.Count),
            new EvalSnapshotInfo(
                snapshot.CommitSha, snapshot.Branch, snapshot.ParserVersion, request.EmbeddingProvider, snapshot.EmbeddingModel),
            new EvalChatInfo(request.ChatProvider, model.ModelId, QuestionPrompt.Version),
            request.Prices is { } prices
                ? new EvalPriceInfo(
                    ModelPrices.Currency,
                    prices.InputPerMillion,
                    prices.OutputPerMillion,
                    prices.CacheReadPerMillion,
                    prices.CacheWritePerMillion)
                : null,
            startedAt,
            EndedAt: timeProvider.GetUtcNow(),
            new EvalTotals(
                results.Count,
                results.Count(result => result.Outcome == EvalOutcomes.Answered),
                results.Count(result => result.Outcome == EvalOutcomes.InsufficientEvidence),
                results.Count(result => result.Outcome == EvalOutcomes.Failed),
                results.Count(result => result.Outcome == EvalOutcomes.Cancelled),
                results.Count(result => result.Outcome == EvalOutcomes.NotRun),
                totalTokens,
                request.Prices is null ? null : results.Sum(result => result.EstimatedCostUsd ?? 0)),
            results);
    }

    private async Task<IndexSnapshot> FindSnapshotAsync(EvalRunRequest request)
    {
        await using var scope = scopes.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<ISnapshotReader>()
            .FindSnapshotAsync(request.RepositoryId, request.SnapshotId);
        return snapshot is { Status: SnapshotStatus.Completed }
            ? snapshot
            : throw new EvalException("The repository has no completed snapshot with the given ID.");
    }

    /// <summary>
    /// Runs one question to a final state. Never throws for anything a question, the model or
    /// the repository did: every such ending is a recorded result, so the run goes on.
    /// </summary>
    private async Task<EvalQuestionResult> RunQuestionAsync(
        EvalRunRequest request,
        EvalQuestion question,
        CancellationToken runCancellation)
    {
        var started = timeProvider.GetTimestamp();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        stop.CancelAfter(request.QuestionTimeout);

        Guid? investigationId = null;
        var fallbackOutcome = EvalOutcomes.Failed;
        string? fallbackError = InternalError;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var investigator = scope.ServiceProvider.GetRequiredService<QuestionInvestigator>();
            var created = await investigator.CreateAsync(
                new AskQuestion(request.RepositoryId, request.SnapshotId, question.Question), stop.Token);
            investigationId = created.Investigation.Id;

            // One attempt and no retry: a second attempt would be a second, unrecorded model run.
            await investigator.RunAsync(investigationId.Value, attempt: 1, maxAttempts: 1, stop.Token);
        }
        catch (InvestigationRequestException)
        {
            fallbackError = RequestRejectedError;
        }
        catch (OperationCanceledException)
        {
            // Only the timeout distinguishes "took too long" from "was cancelled": the run being
            // stopped, or the model call itself being cancelled, are both cancellations.
            var timedOut = stop.IsCancellationRequested && !runCancellation.IsCancellationRequested;
            (fallbackOutcome, fallbackError) = timedOut ? (EvalOutcomes.Failed, "timed_out") : (EvalOutcomes.Cancelled, null);
            await AbandonAsync(investigationId, cancelled: !timedOut, timedOut ? JobProcessor.TimeoutError : null);
        }
        catch (Exception exception)
        {
            // The message may quote the question or source, so only the type is logged.
            logger.LogError(
                "Evaluation question {QuestionId} ended with an unexpected {ExceptionType}.", question.Id, exception.GetType().FullName);
            await AbandonAsync(investigationId, cancelled: false, jobErrorCode: null);
        }

        var duration = timeProvider.GetElapsedTime(started);
        var record = await ReadAsync(investigationId);

        // Measured only for a question the pipeline accepted: that is where the repository's
        // visibility and the snapshot are checked, and the runner makes no such decision itself.
        var retrieval = investigationId is null
            ? null
            : await RetrieveAsync(request, question, runCancellation);
        return Describe(question, fallbackOutcome, fallbackError, record, request.Prices, duration, retrieval);
    }

    /// <summary>
    /// Asks the production search for the question itself, word for word, and records where the
    /// hits are. This is what recall at 10 is computed from. It happens after the investigation,
    /// outside its measured time, and its result is never given to the model.
    /// </summary>
    private async Task<EvalRetrieval?> RetrieveAsync(
        EvalRunRequest request,
        EvalQuestion question,
        CancellationToken runCancellation)
    {
        if (runCancellation.IsCancellationRequested)
        {
            return null;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        stop.CancelAfter(request.QuestionTimeout);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var found = await scope.ServiceProvider.GetRequiredService<SourceTools>().SearchCodeAsync(
                request.RepositoryId, request.SnapshotId, question.Question, RetrievalLimit, stop.Token);
            return new EvalRetrieval(
                RetrievalLimit,
                [.. found.Hits.Select((hit, index) =>
                    new EvalRetrievedLocation(index + 1, hit.Path, hit.StartLine, hit.EndLine, hit.LexicalRank, hit.VectorRank))],
                ErrorCode: null);
        }
        catch (SourceToolException exception)
        {
            // For example a question longer than the search accepts as a query.
            return new EvalRetrieval(RetrievalLimit, [], JsonNamingPolicy.SnakeCaseLower.ConvertName(exception.Error.ToString()));
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return new EvalRetrieval(RetrievalLimit, [], EvalRetrieval.TimedOutError);
        }
        catch (Exception exception)
        {
            // The message may quote the question, so only the type is logged.
            logger.LogError(
                "Retrieval for evaluation question {QuestionId} ended with an unexpected {ExceptionType}.",
                question.Id, exception.GetType().FullName);
            return new EvalRetrieval(RetrievalLimit, [], EvalRetrieval.FailedError);
        }
    }

    /// <summary>Gives an unfinished investigation its final state, as the job queue would after a cancelled or failed job.</summary>
    private async Task AbandonAsync(Guid? investigationId, bool cancelled, string? jobErrorCode)
    {
        if (investigationId is null)
        {
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<QuestionInvestigator>()
                .AbandonAsync(investigationId.Value, cancelled, jobErrorCode, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Could not record the final state of investigation {InvestigationId}: {ExceptionType}.",
                investigationId, exception.GetType().FullName);
        }
    }

    private async Task<InvestigationRecord?> ReadAsync(Guid? investigationId)
    {
        if (investigationId is null)
        {
            return null;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IInvestigationStore>()
                .GetAsync(investigationId.Value, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Could not read back investigation {InvestigationId}: {ExceptionType}.",
                investigationId, exception.GetType().FullName);
            return null;
        }
    }

    /// <summary>
    /// Builds a question's result from what the pipeline stored. The fallback applies only when
    /// the investigation was never created or never reached a final state.
    /// </summary>
    private static EvalQuestionResult Describe(
        EvalQuestion question,
        string fallbackOutcome,
        string? fallbackError,
        InvestigationRecord? record,
        ModelPrices? prices,
        TimeSpan duration,
        EvalRetrieval? retrieval = null)
    {
        var investigation = record?.Investigation;
        var result = investigation is { State: InvestigationState.Completed, ResultJson: { } json }
            ? InvestigationResult.FromJson(json)
            : null;

        var (outcome, errorCode) = investigation?.State switch
        {
            InvestigationState.Completed when result is not null => (
                result.Outcome == InvestigationOutcome.Answered ? EvalOutcomes.Answered : EvalOutcomes.InsufficientEvidence,
                null),
            InvestigationState.Failed => (EvalOutcomes.Failed, investigation.ErrorCode),
            InvestigationState.Cancelled => (EvalOutcomes.Cancelled, null),
            _ => (fallbackOutcome, fallbackError),
        };

        var tokens = new EvalTokens(
            investigation?.InputTokens ?? 0,
            investigation?.OutputTokens ?? 0,
            investigation?.CacheReadTokens ?? 0,
            investigation?.CacheWriteTokens ?? 0);
        var events = record?.Events ?? [];

        return new EvalQuestionResult(
            question.Id,
            question.Split,
            question.Question,
            question.Answerable,
            question.Expected,
            retrieval,
            outcome,
            errorCode,
            investigation?.Id,
            result?.Answer,
            [.. (result?.Evidence ?? []).Select(item =>
                new EvalEvidence(item.Path, item.StartLine, item.EndLine, item.Supports, item.Excerpt))],
            result?.Unknowns ?? [],
            result?.Limitations ?? [],
            [.. events.Select(ToolCall).OfType<EvalToolCall>()],
            events.Count(investigationEvent => investigationEvent.EventType == InvestigationEventType.CitationRejected),
            tokens,
            prices?.Estimate(tokens.Input, tokens.Output, tokens.CacheRead, tokens.CacheWrite),
            (long)duration.TotalMilliseconds);
    }

    /// <summary>A recorded step as a tool call of a result file; null for a step that is not a tool request.</summary>
    internal static EvalToolCall? ToolCall(InvestigationEvent investigationEvent)
    {
        var status = investigationEvent.EventType switch
        {
            InvestigationEventType.ToolCalled => EvalToolCall.Called,
            InvestigationEventType.ToolFailed => EvalToolCall.Failed,
            InvestigationEventType.ToolRejected => EvalToolCall.Rejected,
            _ => null,
        };
        if (status is null)
        {
            return null;
        }

        int? resultCount = null;
        long? latencyMs = null;
        int? outputChars = null;
        string? errorCode = null;
        if (investigationEvent.MetadataJson is { } metadataJson)
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            var root = metadata.RootElement;
            resultCount = root.TryGetProperty("resultCount", out var count) ? count.GetInt32() : null;
            latencyMs = root.TryGetProperty("latencyMs", out var latency) ? latency.GetInt64() : null;
            outputChars = root.TryGetProperty("outputChars", out var chars) ? chars.GetInt32() : null;
            errorCode = root.TryGetProperty("error", out var error) ? error.GetString() : null;
        }

        return new EvalToolCall(
            investigationEvent.Seq,
            investigationEvent.ToolName,
            status,
            resultCount,
            latencyMs,
            outputChars,
            errorCode,
            investigationEvent.Summary);
    }
}
