using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trace.Application.Investigations;
using Trace.Application.Jobs;
using Trace.Application.Plans;
using Trace.Application.Retrieval;
using Trace.Domain;

namespace Trace.Evals;

/// <param name="Split">Run only this split; null runs every labeled issue.</param>
/// <param name="IssueNumbers">Run only the issues with these numbers; null applies no such limit.</param>
/// <param name="IssueTimeout">An issue still running after this long is stopped and recorded as failed with <c>timed_out</c>.</param>
public sealed record PlanEvalRunRequest(
    PlanLabels Labels,
    Guid RepositoryId,
    Guid SnapshotId,
    string ChatProvider,
    string EmbeddingProvider,
    ModelPrices? Prices,
    string TraceCommit,
    bool? TraceCommitDirty,
    TimeSpan IssueTimeout,
    string? Split = null,
    bool AllowRevisionMismatch = false,
    IReadOnlySet<int>? IssueNumbers = null);

/// <summary>
/// Runs labeled issues through the production issue-to-plan pipeline and describes what
/// happened.
/// <para>
/// It contains no planning and no validation logic. Each plan is requested and made by the same
/// <see cref="PlanInvestigator"/> the API and worker use, resolved from the same service
/// registration, as one final job attempt would run it: the server reads the issue, quotes it
/// to the model, and keeps of the model's proposal only what it verified (ADR-017). This class
/// only reads back what the pipeline stored. The repository and snapshot are fixed by the run.
/// </para>
/// </summary>
public sealed class PlanEvalRunner(
    IServiceScopeFactory scopes,
    IChatModel model,
    TimeProvider timeProvider,
    ILogger<PlanEvalRunner> logger)
{
    /// <param name="onIssue">Called after each issue that was run, for progress output.</param>
    /// <param name="cancellationToken">Stops the run: the issue in progress is cancelled and the rest are recorded as not run.</param>
    public async Task<PlanEvalRunResult> RunAsync(
        PlanEvalRunRequest request,
        Action<PlanEvalIssueResult>? onIssue = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.IssueTimeout, TimeSpan.Zero);

        var snapshot = await FindSnapshotAsync(request);
        var revisionMatches = request.Labels.Revision is { } revision ? revision == snapshot.CommitSha : (bool?)null;
        if (revisionMatches == false && !request.AllowRevisionMismatch)
        {
            throw new EvalException(
                $"The issues were labeled against revision {request.Labels.Revision} but the snapshot is {snapshot.CommitSha}. "
                + "The labeled files may no longer be what the labels describe.");
        }

        var labels = request.Labels.Issues
            .Where(label => request.Split is null || label.Split == request.Split)
            .Where(label => request.IssueNumbers is null || request.IssueNumbers.Contains(label.Number))
            .ToList();

        var startedAt = timeProvider.GetUtcNow();
        var results = new List<PlanEvalIssueResult>(labels.Count);
        foreach (var label in labels)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(Describe(label, PlanEvalOutcomes.NotRun, fallbackError: null, record: null, plan: null, request.Prices, TimeSpan.Zero));
                continue;
            }

            var result = await RunIssueAsync(request, label, cancellationToken);
            results.Add(result);
            onIssue?.Invoke(result);
        }

        var totalTokens = new EvalTokens(
            results.Sum(result => result.Tokens.Input),
            results.Sum(result => result.Tokens.Output),
            results.Sum(result => result.Tokens.CacheRead),
            results.Sum(result => result.Tokens.CacheWrite));

        return new PlanEvalRunResult(
            PlanEvalRunResult.CurrentSchemaVersion,
            PlanEvalRunResult.PlanKind,
            RunId: Guid.NewGuid().ToString("N"),
            cancellationToken.IsCancellationRequested ? EvalRunStatus.Cancelled : EvalRunStatus.Completed,
            request.TraceCommit,
            request.TraceCommitDirty,
            new PlanEvalLabelsInfo(
                request.Labels.Name,
                request.Labels.Version,
                request.Labels.Revision,
                revisionMatches,
                request.IssueNumbers is not null ? EvalDatasetInfo.SubsetSplit : request.Split ?? "all",
                labels.Count),
            new EvalSnapshotInfo(
                snapshot.CommitSha, snapshot.Branch, snapshot.ParserVersion, request.EmbeddingProvider, snapshot.EmbeddingModel),
            new PlanEvalChatInfo(request.ChatProvider, model.ModelId, PlanPrompt.Version, PlanResult.CurrentSchemaVersion),
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
            new PlanEvalTotals(
                results.Count,
                results.Count(result => result.Outcome == PlanEvalOutcomes.Planned),
                results.Count(result => result.Outcome == PlanEvalOutcomes.InsufficientEvidence),
                results.Count(result => result.Outcome == PlanEvalOutcomes.Failed),
                results.Count(result => result.Outcome == PlanEvalOutcomes.Cancelled),
                results.Count(result => result.Outcome == PlanEvalOutcomes.NotRun),
                totalTokens,
                request.Prices is null ? null : results.Sum(result => result.EstimatedCostUsd ?? 0)),
            results);
    }

    private async Task<IndexSnapshot> FindSnapshotAsync(PlanEvalRunRequest request)
    {
        await using var scope = scopes.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<ISnapshotReader>()
            .FindSnapshotAsync(request.RepositoryId, request.SnapshotId);
        return snapshot is { Status: SnapshotStatus.Completed }
            ? snapshot
            : throw new EvalException("The repository has no completed snapshot with the given ID.");
    }

    /// <summary>
    /// Runs one issue to a final state. Never throws for anything an issue, the model or the
    /// repository did: every such ending is a recorded result, so the run goes on.
    /// </summary>
    private async Task<PlanEvalIssueResult> RunIssueAsync(
        PlanEvalRunRequest request,
        PlanIssueLabel label,
        CancellationToken runCancellation)
    {
        var started = timeProvider.GetTimestamp();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        stop.CancelAfter(request.IssueTimeout);

        Guid? investigationId = null;
        var fallbackOutcome = PlanEvalOutcomes.Failed;
        string? fallbackError = EvalRunner.InternalError;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var investigator = scope.ServiceProvider.GetRequiredService<PlanInvestigator>();
            var created = await investigator.CreateAsync(
                new RequestPlan(request.RepositoryId, request.SnapshotId, label.Number), stop.Token);
            investigationId = created.Plan.InvestigationId;

            // One attempt and no retry: a second attempt would be a second, unrecorded model run.
            await investigator.RunAsync(investigationId.Value, attempt: 1, maxAttempts: 1, stop.Token);
        }
        catch (InvestigationRequestException)
        {
            fallbackError = EvalRunner.RequestRejectedError;
        }
        catch (OperationCanceledException)
        {
            var timedOut = stop.IsCancellationRequested && !runCancellation.IsCancellationRequested;
            (fallbackOutcome, fallbackError) = timedOut ? (PlanEvalOutcomes.Failed, "timed_out") : (PlanEvalOutcomes.Cancelled, null);
            await AbandonAsync(investigationId, cancelled: !timedOut, timedOut ? JobProcessor.TimeoutError : null);
        }
        catch (Exception exception)
        {
            // The message may quote the issue or source, so only the type is logged.
            logger.LogError(
                "Evaluation issue {IssueNumber} ended with an unexpected {ExceptionType}.", label.Number, exception.GetType().FullName);
            await AbandonAsync(investigationId, cancelled: false, jobErrorCode: null);
        }

        var duration = timeProvider.GetElapsedTime(started);
        var (record, plan) = await ReadAsync(investigationId);
        return Describe(label, fallbackOutcome, fallbackError, record, plan, request.Prices, duration);
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
            await scope.ServiceProvider.GetRequiredService<PlanInvestigator>()
                .AbandonAsync(investigationId.Value, cancelled, jobErrorCode, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Could not record the final state of investigation {InvestigationId}: {ExceptionType}.",
                investigationId, exception.GetType().FullName);
        }
    }

    private async Task<(InvestigationRecord? Record, Plan? Plan)> ReadAsync(Guid? investigationId)
    {
        if (investigationId is null)
        {
            return (null, null);
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var record = await scope.ServiceProvider.GetRequiredService<IInvestigationStore>()
                .GetAsync(investigationId.Value, CancellationToken.None);
            var plan = await scope.ServiceProvider.GetRequiredService<IPlanStore>()
                .GetByInvestigationAsync(investigationId.Value, CancellationToken.None);
            return (record, plan);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Could not read back investigation {InvestigationId}: {ExceptionType}.",
                investigationId, exception.GetType().FullName);
            return (null, null);
        }
    }

    /// <summary>
    /// Builds an issue's result from what the pipeline stored. The fallback applies only when
    /// the investigation was never created or never reached a final state.
    /// </summary>
    private static PlanEvalIssueResult Describe(
        PlanIssueLabel label,
        string fallbackOutcome,
        string? fallbackError,
        InvestigationRecord? record,
        Plan? plan,
        ModelPrices? prices,
        TimeSpan duration)
    {
        var investigation = record?.Investigation;
        var completed = investigation is { State: InvestigationState.Completed, ResultJson: not null } && plan is { ResultJson: not null };

        // Both are read with the production readers: a stored plan this build cannot read is a defect, not a result.
        var stored = completed ? PlanResult.FromJson(plan!.ResultJson!) : null;
        var summary = completed ? PlanRunSummary.FromJson(investigation!.ResultJson!) : null;

        var (outcome, errorCode) = investigation?.State switch
        {
            InvestigationState.Completed when stored is not null => (
                stored.Outcome == PlanOutcome.Planned ? PlanEvalOutcomes.Planned : PlanEvalOutcomes.InsufficientEvidence,
                null),
            InvestigationState.Failed => (PlanEvalOutcomes.Failed, investigation.ErrorCode),
            InvestigationState.Cancelled => (PlanEvalOutcomes.Cancelled, null),
            _ => (fallbackOutcome, fallbackError),
        };

        var tokens = new EvalTokens(
            investigation?.InputTokens ?? 0,
            investigation?.OutputTokens ?? 0,
            investigation?.CacheReadTokens ?? 0,
            investigation?.CacheWriteTokens ?? 0);

        JsonElement? document = null;
        if (stored is not null)
        {
            using var parsed = JsonDocument.Parse(plan!.ResultJson!);
            document = parsed.RootElement.Clone();
        }

        return new PlanEvalIssueResult(
            label.Number,
            label.Split,
            label,
            outcome,
            errorCode,
            // Written by the server from a fixed set of sentences; never the model's words.
            outcome == PlanEvalOutcomes.Failed
                ? record?.Events.LastOrDefault(investigationEvent => investigationEvent.EventType == InvestigationEventType.Failed)?.Summary
                : null,
            investigation?.Id,
            plan?.Id,
            plan?.IssueContentHash,
            document,
            summary?.Plan.Trimmed,
            summary?.Limitations ?? [],
            [.. (record?.Events ?? []).Select(EvalRunner.ToolCall).OfType<EvalToolCall>()],
            tokens,
            prices?.Estimate(tokens.Input, tokens.Output, tokens.CacheRead, tokens.CacheWrite),
            (long)duration.TotalMilliseconds);
    }
}
