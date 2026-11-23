using Trace.Application.Telemetry;
using Trace.Domain;

namespace Trace.UnitTests;

public class TelemetryRuleTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("3f2c9d0e5b7a4c1d8e6f0a1b2c3d4e5f")]
    [InlineData("web.2026-11-23_run-7")]
    public void Plain_short_correlation_ids_are_accepted(string value) =>
        Assert.True(TraceTelemetry.IsValidCorrelationId(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("trailing-newline\n")]
    [InlineData("<script>")]
    [InlineData("quote\"")]
    [InlineData("ünïcode")]
    public void Anything_else_is_not_a_correlation_id(string? value) =>
        Assert.False(TraceTelemetry.IsValidCorrelationId(value));

    [Fact]
    public void Correlation_ids_longer_than_the_limit_are_rejected()
    {
        Assert.True(TraceTelemetry.IsValidCorrelationId(new string('a', TraceTelemetry.MaxCorrelationIdLength)));
        Assert.False(TraceTelemetry.IsValidCorrelationId(new string('a', TraceTelemetry.MaxCorrelationIdLength + 1)));
    }

    [Fact]
    public void Generated_correlation_ids_are_valid_and_distinct()
    {
        var first = TraceTelemetry.NewCorrelationId();
        var second = TraceTelemetry.NewCorrelationId();

        Assert.True(TraceTelemetry.IsValidCorrelationId(first));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Job_keeps_trace_context_that_fits_and_drops_what_does_not()
    {
        var job = Job.Enqueue("kind", "repository:1", "key", "{}", DateTimeOffset.UnixEpoch);
        Assert.Null(job.TraceParent);
        Assert.Null(job.CorrelationId);

        job.AttachTrace("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", "abc");
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", job.TraceParent);
        Assert.Equal("abc", job.CorrelationId);

        job.AttachTrace(new string('x', Job.MaxTraceParentLength + 1), new string('y', Job.MaxCorrelationIdLength + 1));
        Assert.Null(job.TraceParent);
        Assert.Null(job.CorrelationId);
    }

    [Fact]
    public void A_job_span_continues_the_trace_that_queued_it_and_carries_the_correlation_id()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == TraceTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var activity = TraceTelemetry.StartJob(
            "investigation.run", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", "corr-1");

        Assert.NotNull(activity);
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", activity.TraceId.ToString());
        Assert.Equal("b7ad6b7169203331", activity.ParentSpanId.ToString());
        Assert.Equal("corr-1", TraceTelemetry.CorrelationId);
    }

    [Fact]
    public void A_job_with_no_or_malformed_trace_context_starts_a_trace_of_its_own()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == TraceTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        using var activity = TraceTelemetry.StartJob("repository.index", "not a trace parent", correlationId: null);

        Assert.NotNull(activity);
        Assert.Equal(default, activity.ParentSpanId);
        Assert.Null(TraceTelemetry.CorrelationId);
    }
}
