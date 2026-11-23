using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Trace.Application.Telemetry;
using Trace.Contracts.V1;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class TelemetryTests(DemoApi api) : IClassFixture<DemoApi>
{
    [Fact]
    public async Task A_well_formed_correlation_id_is_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/repositories");
        request.Headers.Add(TraceTelemetry.CorrelationHeader, "client-abc.123_x");

        using var response = await api.Client.SendAsync(request);

        Assert.Equal("client-abc.123_x", Assert.Single(response.Headers.GetValues(TraceTelemetry.CorrelationHeader)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("has spaces")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task A_missing_or_malformed_correlation_id_is_replaced_not_echoed(string? supplied)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/repositories");
        if (supplied is not null)
        {
            request.Headers.TryAddWithoutValidation(TraceTelemetry.CorrelationHeader, supplied);
        }

        using var response = await api.Client.SendAsync(request);

        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(response.Headers.GetValues(TraceTelemetry.CorrelationHeader)));
    }

    [Fact]
    public async Task Error_responses_carry_a_correlation_id_too()
    {
        using var response = await api.Client.GetAsync($"/api/v1/investigations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Matches("^[0-9a-f]{32}$", Assert.Single(response.Headers.GetValues(TraceTelemetry.CorrelationHeader)));
    }

    [Fact]
    public async Task One_trace_and_correlation_id_follow_a_question_from_the_request_through_its_job_model_and_tools()
    {
        const string Question = "zzqxprobe why does the first page of the task list skip the first results?";
        var correlationId = $"test-{Guid.NewGuid():N}";
        var repository = await api.DemoRepositoryAsync();

        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TraceTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/investigations")
        {
            Content = JsonContent.Create(new
            {
                repositoryId = repository.Id,
                snapshotId = repository.LatestSnapshot!.Id,
                type = "question",
                question = Question,
            }),
        };
        request.Headers.Add(TraceTelemetry.CorrelationHeader, correlationId);
        using var response = await api.Client.SendAsync(request);
        var accepted = (await response.Content.ReadFromJsonAsync<InvestigationResponse>())!;
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(correlationId, Assert.Single(response.Headers.GetValues(TraceTelemetry.CorrelationHeader)));

        var finished = accepted;
        for (var attempt = 0; attempt < 200 && finished.State is "queued" or "running"; attempt++)
        {
            await Task.Delay(50);
            finished = (await api.Client.GetFromJsonAsync<InvestigationResponse>($"/api/v1/investigations/{accepted.InvestigationId}"))!;
        }

        Assert.Equal("completed", finished.State);

        // The job remembers who queued it.
        await using var db = api.Database.CreateContext();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == accepted.JobId);
        Assert.Equal(correlationId, job.CorrelationId);
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", job.TraceParent);
        var traceId = job.TraceParent!.Split('-')[1];

        // The job span is recorded just after the investigation is saved as finished.
        var ours = Array.Empty<Activity>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            ours = [.. spans.Where(span => span.GetBaggageItem(TraceTelemetry.CorrelationKey) == correlationId)];
            if (ours.Any(span => span.OperationName == "job investigation.run"))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Contains(ours, span => span.OperationName == "job investigation.run");
        Assert.Contains(ours, span => span.OperationName == "investigation.run");
        Assert.Contains(ours, span => span.OperationName == "model.turn");
        Assert.Contains(ours, span => span.OperationName == "tool search_code");
        Assert.All(ours, span => Assert.Equal(traceId, span.TraceId.ToString()));

        var jobSpan = ours.Single(span => span.OperationName == "job investigation.run");
        Assert.Equal("succeeded", jobSpan.GetTagItem("trace.job.outcome"));
        Assert.Equal(job.TraceParent.Split('-')[2], jobSpan.ParentSpanId.ToString());
        var run = ours.Single(span => span.OperationName == "investigation.run");
        Assert.Equal("answered", run.GetTagItem("trace.investigation.outcome"));

        // Spans describe the work; they never contain the question, the search or the source.
        var recorded = ours
            .SelectMany(span => span.TagObjects.Select(tag => $"{tag.Key}={tag.Value}")
                .Concat(span.Events.Select(e => e.Name))
                .Append(span.DisplayName)
                .Append(span.StatusDescription ?? string.Empty))
            .ToList();
        Assert.DoesNotContain(recorded, text => text.Contains("zzqxprobe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recorded, text => text.Contains("task list", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recorded, text => text.Contains("PageSize", StringComparison.Ordinal));
        Assert.DoesNotContain(recorded, text => text.Contains("TaskRepository", StringComparison.Ordinal));

        // The stored step list points back at the trace.
        var started = await db.InvestigationEvents.AsNoTracking()
            .SingleAsync(e => e.InvestigationId == accepted.InvestigationId && e.Seq == 1);
        Assert.Contains($"\"correlationId\":\"{correlationId}\"", started.MetadataJson!.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(traceId, started.MetadataJson, StringComparison.Ordinal);
    }
}
