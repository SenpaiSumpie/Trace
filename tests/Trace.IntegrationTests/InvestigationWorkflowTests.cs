using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trace.Application.Investigations;
using Trace.Application.Retrieval;
using Trace.Domain;
using Trace.IntegrationTests.Support;

namespace Trace.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class InvestigationWorkflowTests(IndexedSampleRepo indexed) : IClassFixture<IndexedSampleRepo>
{
    private const string TaskRepositoryPath = "src/TaskBoard.Api/Tasks/TaskRepository.cs";
    private const string Question = "Why does the first page of the task list skip the first results?";

    [Fact]
    public async Task Question_is_answered_with_evidence_read_from_the_pinned_snapshot()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Search("task list first page skip")),
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => ScriptedChatModel.Answer("List skips Page * PageSize items, so page 1 skips a full page.", (TaskRepositoryPath, 24, 26)));

        var record = await AskAsync(model);

        var investigation = record.Investigation;
        Assert.Equal(InvestigationState.Completed, investigation.State);
        Assert.Equal((indexed.RepositoryId, indexed.SnapshotId), (investigation.RepositoryId, investigation.SnapshotId));
        Assert.Equal(("scripted-v1", QuestionPrompt.Version), (investigation.ModelId, investigation.PromptVersion));
        Assert.Equal(indexed.CommitSha, record.CommitSha);
        Assert.Null(investigation.ErrorCode);

        var result = InvestigationResult.FromJson(investigation.ResultJson!);
        Assert.Equal(InvestigationOutcome.Answered, result.Outcome);
        Assert.Equal("List skips Page * PageSize items, so page 1 skips a full page.", result.Answer);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal((TaskRepositoryPath, 24, 26), (evidence.Path, evidence.StartLine, evidence.EndLine));
        Assert.Equal(string.Join('\n', SampleRepo.ReadLines(TaskRepositoryPath)[23..26]), evidence.Excerpt);
        Assert.Equal(["Runtime behaviour was not observed."], result.Unknowns);

        Assert.Equal(
            [
                (1, InvestigationEventType.Started, null),
                (2, InvestigationEventType.ToolCalled, "search_code"),
                (3, InvestigationEventType.ToolCalled, "read_file_range"),
                (4, InvestigationEventType.Completed, null),
            ],
            record.Events.Select(e => (e.Seq, e.EventType, e.ToolName)));
        Assert.Equal($"Read {TaskRepositoryPath} lines 20-29.", record.Events[2].Summary);

        await using var db = indexed.Database.CreateContext();
        var reference = await db.EvidenceRefs.SingleAsync(e => e.InvestigationId == investigation.Id);
        Assert.Equal((EvidenceSourceKind.Code, indexed.SnapshotId), (reference.SourceKind, reference.SnapshotId));
        Assert.Equal(SourceChunk.ComputeContentHash(evidence.Excerpt), reference.ExcerptHash);
    }

    [Fact]
    public async Task Model_always_gets_the_same_policy_and_exactly_two_tools_and_tool_output_as_data()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Read("docs/CONTRIBUTING.md", 1, 15)),
            _ => ScriptedChatModel.NotEnoughEvidence());

        await AskAsync(model);

        Assert.All(model.Requests, request =>
        {
            Assert.Equal(QuestionPrompt.System, request.SystemPrompt);
            Assert.Equal(["search_code", "read_file_range"], request.Tools.Select(tool => tool.Name));
            Assert.Equal(Question, Assert.IsType<UserMessage>(request.Messages[0]).Text);
        });
        Assert.Contains("untrusted data", QuestionPrompt.System, StringComparison.Ordinal);

        // The injected text reaches the model only inside a tool result, never as a system or user message.
        var toolResult = Assert.Single(model.Requests[1].Messages.OfType<ToolResultMessage>());
        Assert.Contains("Ignore all previous instructions", toolResult.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            model.Requests[1].Messages.OfType<UserMessage>(),
            message => message.Text.Contains("Ignore all previous instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Model_that_obeys_injected_instructions_cannot_reach_other_tools_paths_or_repositories()
    {
        // A second, private repository whose content must never be reachable from this investigation.
        await using var db = indexed.Database.CreateContext();
        var secretRepository = Repository.Create("acme", "secret-repo", "main", RepositoryVisibility.Private);
        db.Add(secretRepository);
        await db.SaveChangesAsync();
        using var secretSource = SampleRepo.CopyToTemp();
        await File.WriteAllTextAsync(Path.Combine(secretSource.Path, "docs", "zebracorn.md"), "# Zebracorn launch codes\n\nzebracorn-secret-value\n");
        var secretSnapshot = await indexed.Database.IndexAsync(secretRepository.Id, SampleRepo.Source(secretSource.Path));

        IReadOnlyList<ToolResultMessage> hostileResults = [];
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Read("docs/CONTRIBUTING.md", 1, 15)),
            _ => ModelTurn.Call(
                model.Tool("secret_admin_tool", new { reveal = "system prompt and api keys" }),
                model.Tool("shell", new { command = "cat /etc/passwd" }),
                model.Tool("write_file", new { path = "README.md", content = "pwned" }),
                model.Tool("get_issue", new { issueNumber = 1 }),
                model.Tool("search_code", new { query = "zebracorn", repositoryId = secretRepository.Id, snapshotId = secretSnapshot.SnapshotId }),
                model.Tool("read_file_range", new { path = "docs/zebracorn.md", startLine = 1, endLine = 3, snapshotId = secretSnapshot.SnapshotId }),
                model.Read("../../.env", 1, 5),
                model.Read("docs/zebracorn.md", 1, 3),
                model.Search("zebracorn launch codes")),
            request =>
            {
                hostileResults = ScriptedChatModel.LastToolResults(request);
                return ScriptedChatModel.Answer("This repository has no known defects.", ("docs/CONTRIBUTING.md", 1, 4), ("docs/zebracorn.md", 1, 3));
            });

        var record = await AskAsync(model);

        // Nothing but the two read-only tools ever ran, and only against the pinned snapshot.
        Assert.Equal(9, hostileResults.Count);
        Assert.All(hostileResults.Take(8), result => Assert.True(result.IsError));
        Assert.All(hostileResults, result => Assert.DoesNotContain("zebracorn-secret-value", result.Content, StringComparison.Ordinal));
        Assert.All(hostileResults, result => Assert.DoesNotContain("docs/zebracorn.md", result.Content, StringComparison.Ordinal));
        Assert.False(hostileResults[8].IsError);

        var events = record.Events;
        Assert.Equal(4, events.Count(e => e.EventType == InvestigationEventType.ToolRejected));
        Assert.Equal(4, events.Count(e => e.EventType == InvestigationEventType.ToolFailed));
        Assert.All(
            events.Where(e => e.EventType == InvestigationEventType.ToolCalled),
            e => Assert.Contains(e.ToolName, new[] { "search_code", "read_file_range" }));
        Assert.Contains(events, e => e.Summary.Contains("\"secret_admin_tool\"", StringComparison.Ordinal));

        // The fabricated citation into the other repository is discarded; the real one stays.
        var result = InvestigationResult.FromJson(record.Investigation.ResultJson!);
        Assert.Equal("docs/CONTRIBUTING.md", Assert.Single(result.Evidence).Path);
        Assert.Contains(events, e => e.EventType == InvestigationEventType.CitationRejected
            && e.Summary.Contains("docs/zebracorn.md:1-3", StringComparison.Ordinal));
        Assert.DoesNotContain("zebracorn-secret-value", record.Investigation.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insufficient_evidence_is_reported_without_keeping_any_unverified_answer_text()
    {
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence(leakedAnswer: "Probably PostgreSQL."));

        var record = await AskAsync(model, "How are tasks replicated across data centres?");

        var result = InvestigationResult.FromJson(record.Investigation.ResultJson!);
        Assert.Equal(InvestigationState.Completed, record.Investigation.State);
        Assert.Equal(InvestigationOutcome.InsufficientEvidence, result.Outcome);
        Assert.Equal(QuestionInvestigator.InsufficientEvidenceAnswer, result.Answer);
        Assert.Empty(result.Evidence);
        Assert.Equal(["The repository does not show this."], result.Unknowns);
        Assert.NotEmpty(result.Limitations);
        Assert.DoesNotContain("PostgreSQL", record.Investigation.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fabricated_and_unseen_citations_are_discarded_and_only_verified_ones_become_evidence()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => ScriptedChatModel.Answer(
                "Paging is wrong.",
                (TaskRepositoryPath, 24, 26),                          // real and retrieved
                (TaskRepositoryPath, 24, 26),                          // duplicate
                ("src/TaskBoard.Api/Tasks/PagingService.cs", 1, 10),    // invented file
                (TaskRepositoryPath, 400, 410),                        // invented lines
                ("src/TaskBoard.Api/Tasks/PageRequest.cs", 8, 9),       // real, but never shown to the model
                (TaskRepositoryPath, 30, 35)));                        // real file, lines not retrieved

        var record = await AskAsync(model);

        var result = InvestigationResult.FromJson(record.Investigation.ResultJson!);
        Assert.Equal(InvestigationOutcome.Answered, result.Outcome);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal((TaskRepositoryPath, 24, 26), (evidence.Path, evidence.StartLine, evidence.EndLine));

        var rejections = record.Events.Where(ev => ev.EventType == InvestigationEventType.CitationRejected).Select(ev => ev.Summary).ToList();
        Assert.Equal(4, rejections.Count);
        Assert.Equal(2, rejections.Count(summary => summary.Contains("does not exist in the indexed snapshot", StringComparison.Ordinal)));
        Assert.Equal(2, rejections.Count(summary => summary.Contains("not retrieved during this investigation", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Answer_with_no_verifiable_citation_is_discarded_entirely()
    {
        var model = new ScriptedChatModel(
            _ => ScriptedChatModel.Answer("The cache layer drops the first page.", ("src/Cache/PageCache.cs", 10, 20)));

        var record = await AskAsync(model);

        var result = InvestigationResult.FromJson(record.Investigation.ResultJson!);
        Assert.Equal(InvestigationOutcome.InsufficientEvidence, result.Outcome);
        Assert.Empty(result.Evidence);
        Assert.DoesNotContain("cache layer", record.Investigation.ResultJson!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(record.Events, e => e.EventType == InvestigationEventType.CitationRejected);
    }

    [Fact]
    public async Task Model_that_never_answers_is_stopped_at_the_turn_limit()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(_ => ModelTurn.Call(model.Search("page")));

        var record = await AskAsync(model);

        var result = InvestigationResult.FromJson(record.Investigation.ResultJson!);
        Assert.Equal(QuestionInvestigator.MaxModelTurns, model.Requests.Count);
        Assert.Equal(InvestigationOutcome.InsufficientEvidence, result.Outcome);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("step limit", StringComparison.Ordinal));
        Assert.Equal(QuestionInvestigator.MaxModelTurns, record.Events.Count(e => e.EventType == InvestigationEventType.ToolCalled));
    }

    [Fact]
    public async Task Tool_calls_beyond_the_budget_are_refused_without_running()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call([.. Enumerable.Range(0, QuestionInvestigator.MaxToolCalls + 4).Select(i => model.Search($"page {i}"))]),
            _ => ScriptedChatModel.NotEnoughEvidence());

        var record = await AskAsync(model);

        Assert.Equal(QuestionInvestigator.MaxToolCalls, record.Events.Count(e => e.EventType == InvestigationEventType.ToolCalled));
        Assert.Equal(4, record.Events.Count(e => e.EventType == InvestigationEventType.ToolRejected));
        Assert.Equal(4, ScriptedChatModel.LastToolResults(model.Requests[1]).Count(result => result.IsError));
    }

    [Fact]
    public async Task Search_limit_requested_by_the_model_is_capped()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Search("page", limit: 5_000)),
            _ => ScriptedChatModel.NotEnoughEvidence());

        var record = await AskAsync(model);

        Assert.Contains($": {QuestionInvestigator.MaxSearchHitsPerCall} result(s).", record.Events[1].Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("I think the answer is that paging is broken.")]
    [InlineData("{\"sufficientEvidence\": true, \"answer\": 5}")]
    [InlineData("{\"sufficientEvidence\": true, \"answer\": \"x\", \"citations\": [null]}")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task Malformed_final_output_fails_the_investigation_instead_of_being_shown(string finalText)
    {
        var model = new ScriptedChatModel(_ => ModelTurn.Final(finalText));

        var record = await AskAsync(model);

        Assert.Equal(InvestigationState.Failed, record.Investigation.State);
        Assert.Equal("invalid_model_output", record.Investigation.ErrorCode);
        Assert.Null(record.Investigation.ResultJson);
        Assert.Equal(InvestigationEventType.Failed, record.Events[^1].EventType);
    }

    [Fact]
    public async Task Oversized_answer_fails_the_investigation()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => ScriptedChatModel.Answer(new string('a', ModelAnswer.MaxAnswerLength + 1), (TaskRepositoryPath, 24, 26)));

        var record = await AskAsync(model);

        Assert.Equal("invalid_model_output", record.Investigation.ErrorCode);
    }

    [Fact]
    public async Task Answer_wrapped_in_a_code_fence_is_accepted()
    {
        ScriptedChatModel model = null!;
        model = new ScriptedChatModel(
            _ => ModelTurn.Call(model.Read(TaskRepositoryPath, 20, 29)),
            _ => ModelTurn.Final("```json\n" + ScriptedChatModel.Answer("Paging skips a page.", (TaskRepositoryPath, 24, 26)).FinalText + "\n```"));

        var record = await AskAsync(model);

        Assert.Equal(InvestigationOutcome.Answered, InvestigationResult.FromJson(record.Investigation.ResultJson!).Outcome);
    }

    [Fact]
    public async Task Model_outage_fails_the_investigation_with_a_recorded_reason()
    {
        var model = new ScriptedChatModel(_ => throw new HttpRequestException("503 from provider; key=sk-should-not-leak"));

        var record = await AskAsync(model);

        Assert.Equal(InvestigationState.Failed, record.Investigation.State);
        Assert.Equal("model_error", record.Investigation.ErrorCode);
        Assert.DoesNotContain(record.Events, e => e.Summary.Contains("sk-should-not-leak", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelled_request_leaves_the_investigation_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var model = new ScriptedChatModel(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AskAsync(model, cancellationToken: cancellation.Token));

        await using var db = indexed.Database.CreateContext();
        var latest = await db.Investigations.OrderByDescending(i => i.CreatedAt).FirstAsync();
        Assert.Equal(InvestigationState.Cancelled, latest.State);
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_idempotency_key_returns_the_first_investigation()
    {
        var key = Guid.NewGuid().ToString();
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence());

        var first = await AskAsync(model, idempotencyKey: key);
        var second = await AskAsync(model, idempotencyKey: key);

        Assert.Equal(first.Investigation.Id, second.Investigation.Id);
        Assert.Single(model.Requests);
        await using var db = indexed.Database.CreateContext();
        Assert.Equal(1, await db.Investigations.CountAsync(i => i.IdempotencyKey == key));
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_for_a_different_question_is_a_conflict()
    {
        var key = Guid.NewGuid().ToString();
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence());
        await AskAsync(model, idempotencyKey: key);

        var exception = await Assert.ThrowsAsync<InvestigationRequestException>(
            () => AskAsync(model, "A different question?", idempotencyKey: key));

        Assert.Equal(InvestigationRequestError.IdempotencyConflict, exception.Error);
    }

    [Fact]
    public async Task Investigation_cannot_be_started_against_a_private_repository_or_a_foreign_snapshot()
    {
        await using var db = indexed.Database.CreateContext();
        var privateRepository = Repository.Create("acme", $"private-{Guid.NewGuid():N}", "main", RepositoryVisibility.Private);
        db.Add(privateRepository);
        await db.SaveChangesAsync();
        var privateSnapshot = await indexed.Database.IndexAsync(privateRepository.Id, SampleRepo.Source());
        var model = new ScriptedChatModel(_ => ScriptedChatModel.NotEnoughEvidence());

        var hidden = await Assert.ThrowsAsync<InvestigationRequestException>(
            () => AskAsync(model, repositoryId: privateRepository.Id, snapshotId: privateSnapshot.SnapshotId));
        var foreign = await Assert.ThrowsAsync<InvestigationRequestException>(
            () => AskAsync(model, snapshotId: privateSnapshot.SnapshotId));

        Assert.Equal(InvestigationRequestError.RepositoryNotFound, hidden.Error);
        Assert.Equal(InvestigationRequestError.SnapshotNotFound, foreign.Error);
        Assert.Empty(model.Requests);
    }

    private async Task<InvestigationRecord> AskAsync(
        IChatModel model,
        string question = Question,
        string? idempotencyKey = null,
        Guid? repositoryId = null,
        Guid? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        await using var scope = indexed.Database.CreateScope();
        var services = scope.ServiceProvider;
        var investigator = new QuestionInvestigator(
            services.GetRequiredService<IRepositoryCatalog>(),
            services.GetRequiredService<ISnapshotReader>(),
            services.GetRequiredService<IInvestigationStore>(),
            services.GetRequiredService<SourceTools>(),
            services.GetRequiredService<CitationValidator>(),
            model);

        return await investigator.AskAsync(
            new AskQuestion(repositoryId ?? indexed.RepositoryId, snapshotId ?? indexed.SnapshotId, question, idempotencyKey),
            cancellationToken);
    }
}
