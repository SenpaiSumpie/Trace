using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Trace.Contracts.V1;
using Trace.Domain;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

/// <summary>
/// The API as a client sees it, running exactly as in local development: migrated and seeded
/// on startup, with the fixture indexed and the deterministic fake embedding and chat models.
/// </summary>
public sealed class DemoApi(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> factory = new();

    public TestDatabase Database { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await postgres.CreateDatabaseAsync(migrate: false);
        Client = factory.WithDatabase(Database.ConnectionString, initializeOnStartup: true).CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await factory.DisposeAsync();
        await Database.DisposeAsync();
    }

    public async Task<RepositoryResponse> DemoRepositoryAsync() =>
        Assert.Single((await Client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories"))!);
}

[Collection(PostgresCollection.Name)]
public class InvestigationApiTests(DemoApi api) : IClassFixture<DemoApi>
{
    private const string Question = "Why does the first page of the task list skip the first results?";

    [Fact]
    public async Task Repositories_lists_the_demo_repository_with_its_latest_snapshot()
    {
        var repository = await api.DemoRepositoryAsync();

        Assert.Equal(("trace-demo", "sample-repo", "main"), (repository.Owner, repository.Name, repository.DefaultBranch));
        Assert.NotNull(repository.LatestSnapshot);
        Assert.Matches("^[0-9a-f]{40}$", repository.LatestSnapshot.CommitSha);
    }

    [Fact]
    public async Task Asking_a_fixture_question_is_accepted_and_completes_with_inspectable_evidence_and_events()
    {
        var repository = await api.DemoRepositoryAsync();

        using var response = await PostAsync(repository.Id, repository.LatestSnapshot!.Id, Question);
        var accepted = await response.Content.ReadFromJsonAsync<InvestigationResponse>();

        // The request only queues the work.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(accepted);
        Assert.Equal($"/api/v1/investigations/{accepted.InvestigationId}", response.Headers.Location!.AbsolutePath);
        Assert.Contains(accepted.State, new[] { "queued", "running", "completed" });
        Assert.NotNull(accepted.JobId);

        var finished = await WaitUntilFinishedAsync(accepted.InvestigationId);
        Assert.Equal(("question", Question, "completed"), (finished.Type, finished.Question, finished.State));
        Assert.Equal(repository.LatestSnapshot.CommitSha, finished.CommitSha);
        Assert.Equal(("fake-extractive-v1", "question-v1"), (finished.ModelId, finished.PromptVersion));
        Assert.Null(finished.ErrorCode);

        var result = finished.Result!;
        Assert.Equal("answered", result.Outcome);
        Assert.StartsWith("Extractive test model", result.Answer, StringComparison.Ordinal);
        Assert.NotEmpty(result.Evidence);
        Assert.Contains(result.Evidence, evidence => evidence.Path == "src/TaskBoard.Api/Tasks/TaskRepository.cs");
        Assert.All(result.Evidence, evidence =>
        {
            Assert.Equal("code", evidence.Source);
            Assert.Equal(finished.CommitSha, evidence.CommitSha);
            var lines = SampleRepo.ReadLines(evidence.Path);
            Assert.Equal(string.Join('\n', lines[(evidence.StartLine - 1)..evidence.EndLine]), evidence.Excerpt);
        });

        var events = await api.Client.GetFromJsonAsync<List<InvestigationEventResponse>>($"{response.Headers.Location}/events");
        Assert.NotNull(events);
        Assert.Equal(["started", "tool_called", "completed"], events.Select(e => e.EventType));
        Assert.Equal([1, 2, 3], events.Select(e => e.Seq));
        Assert.Equal("search_code", events[1].ToolName);

        var job = await api.Client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{accepted.JobId}");
        Assert.Equal(("investigation.run", "succeeded", 1), (job!.Kind, job.Status, job.Attempts));
        Assert.False(job.CancelRequested);
    }

    [Fact]
    public async Task Question_with_no_matching_source_reports_insufficient_evidence()
    {
        var repository = await api.DemoRepositoryAsync();

        using var response = await PostAsync(repository.Id, repository.LatestSnapshot!.Id, "xyzzy qwfpgj zxcvbnm");
        var accepted = await response.Content.ReadFromJsonAsync<InvestigationResponse>();
        var finished = await WaitUntilFinishedAsync(accepted!.InvestigationId);

        Assert.Equal("completed", finished.State);
        Assert.Equal("insufficient_evidence", finished.Result!.Outcome);
        Assert.Empty(finished.Result.Evidence);
        Assert.NotEmpty(finished.Result.Limitations);
    }

    [Fact]
    public async Task Cancelling_a_job_is_accepted_and_is_harmless_once_it_has_finished()
    {
        var repository = await api.DemoRepositoryAsync();
        using var response = await PostAsync(repository.Id, repository.LatestSnapshot!.Id, Question);
        var accepted = await response.Content.ReadFromJsonAsync<InvestigationResponse>();
        var finished = await WaitUntilFinishedAsync(accepted!.InvestigationId);

        using var cancel = await api.Client.PostAsync($"/api/v1/jobs/{accepted.JobId}/cancel", content: null);
        var job = await cancel.Content.ReadFromJsonAsync<JobResponse>();

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.Equal("succeeded", job!.Status);
        Assert.Equal("completed", (await WaitUntilFinishedAsync(finished.InvestigationId)).State);
    }

    [Fact]
    public async Task Re_indexing_is_queued_as_a_job_and_an_unchanged_repository_keeps_its_snapshot()
    {
        var repository = await api.DemoRepositoryAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/repositories/{repository.Id}/index-jobs");
        request.Headers.Add("Idempotency-Key", "reindex-once");

        using var response = await api.Client.SendAsync(request);
        var job = await response.Content.ReadFromJsonAsync<JobResponse>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("repository.index", job!.Kind);
        Assert.Equal($"/api/v1/jobs/{job.Id}", response.Headers.Location!.AbsolutePath);

        var finished = await WaitUntilJobFinishedAsync(job.Id);
        Assert.Equal("succeeded", finished.Status);
        Assert.Equal(repository.LatestSnapshot!.Id, (await api.DemoRepositoryAsync()).LatestSnapshot!.Id);
    }

    [Fact]
    public async Task Unknown_job_or_repository_is_not_found_for_job_endpoints()
    {
        using var job = await api.Client.GetAsync($"/api/v1/jobs/{Guid.NewGuid()}");
        using var cancel = await api.Client.PostAsync($"/api/v1/jobs/{Guid.NewGuid()}/cancel", content: null);
        using var index = await api.Client.PostAsync($"/api/v1/repositories/{Guid.NewGuid()}/index-jobs", content: null);

        Assert.All(new[] { job, cancel, index }, response =>
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        });
    }

    [Fact]
    public async Task Repeated_request_with_an_idempotency_key_returns_the_same_investigation()
    {
        var repository = await api.DemoRepositoryAsync();
        var key = Guid.NewGuid().ToString();

        using var first = await PostAsync(repository.Id, repository.LatestSnapshot!.Id, Question, idempotencyKey: key);
        using var second = await PostAsync(repository.Id, repository.LatestSnapshot.Id, Question, idempotencyKey: key);
        using var conflicting = await PostAsync(repository.Id, repository.LatestSnapshot.Id, "Something else?", idempotencyKey: key);

        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<InvestigationResponse>())!.InvestigationId,
            (await second.Content.ReadFromJsonAsync<InvestigationResponse>())!.InvestigationId);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Equal("application/problem+json", conflicting.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("""{"repositoryId":"{repo}","snapshotId":"{snap}","type":"question"}""", "question")]
    [InlineData("""{"repositoryId":"{repo}","snapshotId":"{snap}","type":"question","question":"   "}""", "question")]
    [InlineData("""{"repositoryId":"{repo}","snapshotId":"{snap}","type":"issue_to_plan","question":"q"}""", "type")]
    [InlineData("""{"repositoryId":"{repo}","snapshotId":"{snap}","question":"q"}""", "type")]
    [InlineData("""{"snapshotId":"{snap}","type":"question","question":"q"}""", "repositoryId")]
    [InlineData("""{"repositoryId":"{repo}","type":"question","question":"q"}""", "snapshotId")]
    [InlineData("""{"repositoryId":"{repo}","snapshotId":"{snap}","type":"question","question":"q","idempotencyKey":""}""", "idempotencyKey")]
    public async Task Invalid_request_is_rejected_with_validation_problem_details(string bodyTemplate, string invalidField)
    {
        var repository = await api.DemoRepositoryAsync();
        var body = bodyTemplate
            .Replace("{repo}", repository.Id.ToString(), StringComparison.Ordinal)
            .Replace("{snap}", repository.LatestSnapshot!.Id.ToString(), StringComparison.Ordinal);

        using var response = await PostRawAsync(body);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(invalidField, out _));
    }

    [Fact]
    public async Task Overlong_question_is_rejected()
    {
        var repository = await api.DemoRepositoryAsync();

        using var response = await PostAsync(
            repository.Id, repository.LatestSnapshot!.Id, new string('q', Investigation.MaxQuestionLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"repositoryId":"not-a-guid","snapshotId":"x","type":"question","question":"q"}""")]
    public async Task Malformed_body_is_a_bad_request_without_internals(string body)
    {
        using var response = await PostRawAsync(body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_repository_snapshot_or_investigation_is_not_found()
    {
        var repository = await api.DemoRepositoryAsync();

        using var unknownRepository = await PostAsync(Guid.NewGuid(), repository.LatestSnapshot!.Id, Question);
        using var unknownSnapshot = await PostAsync(repository.Id, Guid.NewGuid(), Question);
        using var unknownInvestigation = await api.Client.GetAsync($"/api/v1/investigations/{Guid.NewGuid()}");
        using var unknownEvents = await api.Client.GetAsync($"/api/v1/investigations/{Guid.NewGuid()}/events");

        Assert.All(
            new[] { unknownRepository, unknownSnapshot, unknownInvestigation, unknownEvents },
            response =>
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            });
    }

    [Fact]
    public async Task Private_repository_is_invisible_to_the_unauthenticated_api()
    {
        // Until authentication exists nothing private may be reachable, even by exact ID.
        await using var db = api.Database.CreateContext();
        var privateRepository = Repository.Create("acme", $"private-{Guid.NewGuid():N}", "main", RepositoryVisibility.Private);
        db.Add(privateRepository);
        await db.SaveChangesAsync();
        var snapshot = await api.Database.IndexAsync(privateRepository.Id, SampleRepo.Source());
        var completedSnapshot = await db.IndexSnapshots.SingleAsync(s => s.Id == snapshot.SnapshotId);
        var investigation = Investigation.Create(completedSnapshot, InvestigationType.Question, "private question");
        db.Add(investigation);
        await db.SaveChangesAsync();

        var listed = await api.Client.GetFromJsonAsync<List<RepositoryResponse>>("/api/v1/repositories");
        using var ask = await PostAsync(privateRepository.Id, snapshot.SnapshotId, Question);
        using var read = await api.Client.GetAsync($"/api/v1/investigations/{investigation.Id}");
        using var events = await api.Client.GetAsync($"/api/v1/investigations/{investigation.Id}/events");

        Assert.DoesNotContain(listed!, repository => repository.Id == privateRepository.Id);
        Assert.Equal(HttpStatusCode.NotFound, ask.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, events.StatusCode);
    }

    private Task<HttpResponseMessage> PostAsync(
        Guid repositoryId,
        Guid snapshotId,
        string question,
        string? idempotencyKey = null) =>
        api.Client.PostAsJsonAsync(
            "/api/v1/investigations",
            new CreateInvestigationRequest(repositoryId, snapshotId, "question", question, idempotencyKey));

    private async Task<InvestigationResponse> WaitUntilFinishedAsync(Guid investigationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var investigation = await api.Client.GetFromJsonAsync<InvestigationResponse>($"/api/v1/investigations/{investigationId}");
            if (investigation!.State is "completed" or "failed" or "cancelled")
            {
                return investigation;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The investigation did not finish in time.");
    }

    private async Task<JobResponse> WaitUntilJobFinishedAsync(Guid jobId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var job = await api.Client.GetFromJsonAsync<JobResponse>($"/api/v1/jobs/{jobId}");
            if (job!.Status is "succeeded" or "failed" or "cancelled")
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The job did not finish in time.");
    }

    private Task<HttpResponseMessage> PostRawAsync(string body) =>
        api.Client.PostAsync("/api/v1/investigations", new StringContent(body, Encoding.UTF8, "application/json"));
}
