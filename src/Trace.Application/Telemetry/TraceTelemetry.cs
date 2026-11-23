using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

namespace Trace.Application.Telemetry;

/// <summary>
/// The spans and measurements TRACE records about its own work.
/// <para>
/// Nothing recorded here may contain source code, questions, search queries, model output,
/// file contents, issue titles, bodies or comments, tokens or keys. Spans carry identifiers, counts, durations and fixed
/// outcome names only.
/// </para>
/// </summary>
public static partial class TraceTelemetry
{
    /// <summary>Name of the activity source and the meter; hosts subscribe to both by this name.</summary>
    public const string SourceName = "Trace";

    /// <summary>Request header that carries the correlation ID in and out of the API.</summary>
    public const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>Baggage and span tag under which the correlation ID travels through one trace.</summary>
    public const string CorrelationKey = "trace.correlation_id";

    public const int MaxCorrelationIdLength = 64;

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    /// <summary>Time a job spent queued before a worker claimed it.</summary>
    public static readonly Histogram<double> JobQueueWait =
        Meter.CreateHistogram<double>("trace.job.queue_wait", "s", "Time from a job becoming available to a worker claiming it.");

    public static readonly Histogram<double> JobDuration =
        Meter.CreateHistogram<double>("trace.job.duration", "s", "Duration of one job attempt.");

    public static readonly Histogram<double> InvestigationDuration =
        Meter.CreateHistogram<double>("trace.investigation.duration", "s", "Duration of one investigation run, end to end.");

    public static readonly Histogram<double> ModelTurnDuration =
        Meter.CreateHistogram<double>("trace.model.turn.duration", "s", "Duration of one chat model call.");

    public static readonly Histogram<double> ToolDuration =
        Meter.CreateHistogram<double>("trace.tool.duration", "s", "Duration of one retrieval tool call.");

    public static readonly Histogram<double> IndexDuration =
        Meter.CreateHistogram<double>("trace.index.duration", "s", "Duration of indexing one snapshot.");

    public static readonly Histogram<double> IssueReadDuration =
        Meter.CreateHistogram<double>("trace.issues.read.duration", "s", "Duration of listing or fetching issues, by source and outcome.");

    public static readonly Counter<long> ModelTokens =
        Meter.CreateCounter<long>("trace.model.tokens", "{token}", "Chat model tokens used, by direction.");

    /// <summary>The correlation ID of the work in progress, if there is one.</summary>
    public static string? CorrelationId => Activity.Current?.GetBaggageItem(CorrelationKey);

    /// <summary>
    /// The W3C trace context of the work in progress, to be stored with a job so the worker
    /// that runs it later continues the same trace.
    /// </summary>
    public static string? TraceParent => Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity ? activity.Id : null;

    /// <summary>
    /// A caller-supplied correlation ID is used only if it is short and made of plain
    /// characters: it is written to logs and response headers.
    /// </summary>
    public static bool IsValidCorrelationId(string? value) =>
        value is { Length: > 0 and <= MaxCorrelationIdLength } && CorrelationIdPattern().IsMatch(value);

    public static string NewCorrelationId() => Guid.NewGuid().ToString("N");

    /// <summary>Attaches a correlation ID to the current activity and everything started under it.</summary>
    public static void SetCorrelationId(Activity? activity, string correlationId)
    {
        activity?.SetBaggage(CorrelationKey, correlationId);
        activity?.SetTag(CorrelationKey, correlationId);
    }

    /// <summary>
    /// Starts the span for one job attempt as a child of the request that queued the job, so
    /// one trace covers the request, the wait and the work.
    /// </summary>
    public static Activity? StartJob(string kind, string? traceParent, string? correlationId)
    {
        ActivityContext.TryParse(traceParent, traceState: null, out var parent);
        var activity = Source.StartActivity($"job {kind}", ActivityKind.Consumer, parent);
        if (correlationId is not null)
        {
            SetCorrelationId(activity, correlationId);
        }

        return activity;
    }

    /// <summary>Marks a span as failed with a fixed error name; exception messages are not recorded.</summary>
    public static void Fail(Activity? activity, string errorCode)
    {
        activity?.SetStatus(ActivityStatusCode.Error, errorCode);
        activity?.SetTag("error.type", errorCode);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z")]
    private static partial Regex CorrelationIdPattern();
}
