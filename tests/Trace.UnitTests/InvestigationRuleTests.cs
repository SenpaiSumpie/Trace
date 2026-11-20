using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Domain;
using Trace.Infrastructure.Chat;

namespace Trace.UnitTests;

public class InvestigationRuleTests
{
    [Fact]
    public void Investigation_moves_from_queued_to_running_to_completed()
    {
        var investigation = NewInvestigation();

        investigation.Start("model-v1", "prompt-v1");
        investigation.Complete("{}");

        Assert.Equal(InvestigationState.Completed, investigation.State);
        Assert.Equal(("model-v1", "prompt-v1", "{}"), (investigation.ModelId, investigation.PromptVersion, investigation.ResultJson));
        Assert.Null(investigation.ErrorCode);
    }

    [Fact]
    public void Investigation_records_why_it_failed()
    {
        var investigation = NewInvestigation();
        investigation.Start("model-v1", "prompt-v1");

        investigation.Fail("model_error");

        Assert.Equal((InvestigationState.Failed, "model_error"), (investigation.State, investigation.ErrorCode));
        Assert.Null(investigation.ResultJson);
    }

    [Fact]
    public void Investigation_rejects_out_of_order_transitions()
    {
        var queued = NewInvestigation();
        Assert.Throws<InvalidOperationException>(() => queued.Complete("{}"));

        var finished = NewInvestigation();
        finished.Start("m", "p");
        finished.Complete("{}");
        Assert.Throws<InvalidOperationException>(() => finished.Start("m", "p"));
        Assert.Throws<InvalidOperationException>(() => finished.Fail("x"));
        Assert.Throws<InvalidOperationException>(() => finished.Complete("{}"));
        Assert.Throws<InvalidOperationException>(finished.Cancel);
    }

    [Fact]
    public void Investigation_can_be_cancelled_or_failed_before_it_starts_and_restarted_while_running()
    {
        var cancelledWhileQueued = NewInvestigation();
        cancelledWhileQueued.Cancel();
        Assert.Equal(InvestigationState.Cancelled, cancelledWhileQueued.State);

        var failedWhileQueued = NewInvestigation();
        failedWhileQueued.Fail("worker_interrupted");
        Assert.Equal(InvestigationState.Failed, failedWhileQueued.State);

        var interrupted = NewInvestigation();
        interrupted.Start("m", "p");
        interrupted.Start("m", "p");
        Assert.Equal(InvestigationState.Running, interrupted.State);
    }

    [Fact]
    public void Model_answer_parses_the_documented_shape()
    {
        var answer = ModelAnswer.Parse("""
            {"sufficientEvidence": true, "answer": "Because.", "unknowns": ["u"],
             "citations": [{"path": "a.cs", "startLine": 1, "endLine": 2, "supports": "s"}], "extra": "ignored"}
            """);

        Assert.True(answer.SufficientEvidence);
        Assert.Equal("Because.", answer.Answer);
        Assert.Equal(new ModelCitation("a.cs", 1, 2, "s"), Assert.Single(answer.Citations!));
        Assert.Equal(["u"], answer.Unknowns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("null")]
    [InlineData("plain prose")]
    [InlineData("{\"sufficientEvidence\": \"yes\"}")]
    [InlineData("{\"citations\": [{\"startLine\": \"one\"}]}")]
    public void Model_answer_rejects_anything_else(string? text)
    {
        Assert.Throws<InvalidModelOutputException>(() => ModelAnswer.Parse(text));
    }

    [Fact]
    public void Model_answer_rejects_too_many_citations()
    {
        var citations = string.Join(',', Enumerable.Repeat("{\"path\":\"a.cs\",\"startLine\":1,\"endLine\":1}", ModelAnswer.MaxCitations + 1));

        Assert.Throws<InvalidModelOutputException>(
            () => ModelAnswer.Parse($"{{\"sufficientEvidence\":true,\"answer\":\"x\",\"citations\":[{citations}]}}"));
    }

    [Fact]
    public void Stored_result_round_trips_with_readable_outcome()
    {
        var result = new InvestigationResult(
            InvestigationResult.CurrentSchemaVersion,
            InvestigationOutcome.InsufficientEvidence,
            "answer",
            [new EvidenceItem("a.cs", 1, 2, "supports", "excerpt")],
            ["unknown"],
            ["limitation"]);

        var json = result.ToJson();
        var restored = InvestigationResult.FromJson(json);

        Assert.Contains("\"outcome\":\"insufficient_evidence\"", json, StringComparison.Ordinal);
        Assert.Equal(result.Evidence, restored.Evidence);
        Assert.Equal((result.Outcome, result.Answer, result.SchemaVersion), (restored.Outcome, restored.Answer, restored.SchemaVersion));
    }

    [Fact]
    public async Task Fake_chat_model_searches_with_the_question_then_cites_only_passages_that_matched_its_words()
    {
        var model = new DeterministicFakeChatModel();
        var question = new UserMessage("where is paging?");

        var first = await model.CompleteAsync(new ModelRequest("system", [question], QuestionPrompt.Tools));
        var call = Assert.Single(first.ToolCalls);
        Assert.Equal(QuestionPrompt.SearchCode, call.Name);
        Assert.Equal("where is paging?", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("query").GetString());

        var searchOutput = """
            {"hits":[
              {"path":"a.cs","startLine":1,"endLine":9,"lexicalRank":1,"vectorRank":2,"text":"..."},
              {"path":"b.cs","startLine":5,"endLine":7,"lexicalRank":null,"vectorRank":1,"text":"..."}]}
            """;
        var second = await model.CompleteAsync(new ModelRequest(
            "system",
            [question, new AssistantToolCallsMessage([call]), new ToolResultMessage(call.Id, call.Name, searchOutput, false)],
            QuestionPrompt.Tools));

        var answer = ModelAnswer.Parse(second.FinalText);
        Assert.True(answer.SufficientEvidence);
        Assert.Equal(("a.cs", 1, 9), Assert.Single(answer.Citations!) is { } c ? (c.Path!, c.StartLine, c.EndLine) : default);
        Assert.Contains("no language model interpreted", answer.Answer!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"hits\":[]}", false)]
    [InlineData("{\"hits\":[{\"path\":\"b.cs\",\"startLine\":1,\"endLine\":2,\"lexicalRank\":null}]}", false)]
    [InlineData("{\"error\":{\"code\":\"x\"}}", true)]
    public async Task Fake_chat_model_reports_insufficient_evidence_when_nothing_matched(string toolOutput, bool isError)
    {
        var model = new DeterministicFakeChatModel();

        var turn = await model.CompleteAsync(new ModelRequest(
            "system",
            [new UserMessage("q"), new ToolResultMessage("call-1", QuestionPrompt.SearchCode, toolOutput, isError)],
            QuestionPrompt.Tools));

        var answer = ModelAnswer.Parse(turn.FinalText);
        Assert.False(answer.SufficientEvidence);
        Assert.Empty(answer.Citations!);
    }

    private static Investigation NewInvestigation()
    {
        var snapshot = IndexSnapshot.Start(Guid.NewGuid(), new string('a', 40), "main", "p", "m");
        snapshot.Complete(DateTimeOffset.UtcNow);
        return Investigation.Create(snapshot, InvestigationType.Question, "q");
    }
}
