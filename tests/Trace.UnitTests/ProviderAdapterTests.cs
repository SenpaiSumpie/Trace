using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Trace.Application.Indexing;
using Trace.Application.Investigations;
using Trace.Domain;
using Trace.Infrastructure.Chat;
using Trace.Infrastructure.Embeddings;

namespace Trace.UnitTests;

/// <summary>
/// The real provider adapters against canned HTTP responses in the providers' documented wire
/// formats. These check our translation in both directions; they do not call the real APIs.
/// </summary>
public class ProviderAdapterTests
{
    private const string ToolUseResponse = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[
           {"type":"thinking","thinking":"","signature":"sig-abc"},
           {"type":"text","text":"Let me search."},
           {"type":"tool_use","id":"toolu_1","name":"search_code","input":{"query":"page size","limit":3}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":100,"output_tokens":20,"cache_creation_input_tokens":30,"cache_read_input_tokens":5}}
        """;

    [Fact]
    public async Task Claude_request_carries_the_prompt_tools_effort_and_conversation()
    {
        var http = new StubHandler(_ => Json(ToolUseResponse));
        var model = Claude(http);

        await model.CompleteAsync(new ModelRequest(
            "SYSTEM RULES",
            [
                new UserMessage("Where is paging?"),
                new AssistantToolCallsMessage([new ToolCall("toolu_0", "search_code", """{"query":"paging"}""")]),
                new ToolResultMessage("toolu_0", "search_code", """{"hits":[]}""", IsError: false),
                new ToolResultMessage("toolu_9", "read_file_range", """{"error":{"code":"path_not_found"}}""", IsError: true),
            ],
            QuestionPrompt.Tools));

        var request = Assert.Single(http.Requests);
        Assert.Equal("/v1/messages", request.Path);
        var credential = request.Headers.GetValueOrDefault("x-api-key") ?? request.Headers.GetValueOrDefault("Authorization");
        // The SDK decides the header; what matters is that the configured key, and only it, is the credential.
        Assert.Contains(credential, new[] { "sk-ant-api03-test-key", "Bearer sk-ant-api03-test-key" });

        var body = request.Body.RootElement;
        Assert.Equal("claude-opus-5-5", body.GetProperty("model").GetString());
        Assert.Equal("medium", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Contains("SYSTEM RULES", body.GetProperty("system").ToString(), StringComparison.Ordinal);
        Assert.False(body.TryGetProperty("tool_choice", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal(
            ["search_code", "read_file_range"],
            body.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()));
        Assert.Equal(
            ["query"],
            body.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("required").EnumerateArray().Select(r => r.GetString()));

        var messages = body.GetProperty("messages");
        Assert.Equal(["user", "assistant", "user"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("tool_use", messages[1].GetProperty("content")[0].GetProperty("type").GetString());

        // Both tool results go back together in one user message, errors flagged.
        var results = messages[2].GetProperty("content");
        Assert.Equal(["toolu_0", "toolu_9"], results.EnumerateArray().Select(r => r.GetProperty("tool_use_id").GetString()));
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task Claude_tool_use_response_becomes_tool_calls_with_usage_and_is_replayed_unchanged()
    {
        var http = new StubHandler(_ => Json(ToolUseResponse));
        var model = Claude(http);
        var question = new UserMessage("Where is paging?");

        var turn = await model.CompleteAsync(new ModelRequest("s", [question], QuestionPrompt.Tools));

        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal(("toolu_1", "search_code"), (call.Id, call.Name));
        Assert.Equal("page size", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("query").GetString());
        Assert.Null(turn.FinalText);
        Assert.Equal(new ModelUsage(135, 20), turn.Usage);

        // The next request must send the assistant turn back exactly, thinking signature included.
        await model.CompleteAsync(new ModelRequest(
            "s",
            [question, new AssistantToolCallsMessage(turn.ToolCalls, turn.ProviderState), new ToolResultMessage("toolu_1", "search_code", "{}", false)],
            QuestionPrompt.Tools));

        var replayed = http.Requests[1].Body.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(["thinking", "text", "tool_use"], replayed.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("sig-abc", replayed[0].GetProperty("signature").GetString());
        Assert.Equal("toolu_1", replayed[2].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Claude_final_text_is_returned_as_the_answer()
    {
        var http = new StubHandler(_ => Json("""
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":"{\"sufficientEvidence\":false}"}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":4}}
            """));

        var turn = await Claude(http).CompleteAsync(new ModelRequest("s", [new UserMessage("q")], QuestionPrompt.Tools));

        Assert.Empty(turn.ToolCalls);
        Assert.Equal("{\"sufficientEvidence\":false}", turn.FinalText);
        Assert.Equal(new ModelUsage(10, 4), turn.Usage);
    }

    [Fact]
    public async Task Claude_refusal_is_reported_as_a_refusal_not_an_answer()
    {
        var http = new StubHandler(_ => Json("""
            {"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],
             "stop_reason":"refusal","stop_sequence":null,
             "stop_details":{"type":"refusal","category":"cyber","explanation":"declined"},
             "usage":{"input_tokens":10,"output_tokens":0}}
            """));

        await Assert.ThrowsAsync<ChatModelRefusedException>(
            () => Claude(http).CompleteAsync(new ModelRequest("s", [new UserMessage("q")], QuestionPrompt.Tools)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Claude_api_error_becomes_a_model_error_without_the_provider_message(HttpStatusCode status)
    {
        var http = new StubHandler(_ => Json("""{"type":"error","error":{"type":"x","message":"secret-detail sk-ant-123"}}""", status));

        var exception = await Assert.ThrowsAsync<ChatModelException>(
            () => Claude(http).CompleteAsync(new ModelRequest("s", [new UserMessage("q")], QuestionPrompt.Tools)));

        Assert.DoesNotContain("sk-ant-123", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claude_response_cut_off_at_the_output_limit_is_an_error()
    {
        var http = new StubHandler(_ => Json("""
            {"id":"msg_4","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":"{\"answer\":\"trunc"}],
             "stop_reason":"max_tokens","stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":16000}}
            """));

        await Assert.ThrowsAsync<ChatModelException>(
            () => Claude(http).CompleteAsync(new ModelRequest("s", [new UserMessage("q")], QuestionPrompt.Tools)));
    }

    [Fact]
    public async Task Voyage_request_names_the_model_dimension_and_input_type_and_never_truncates()
    {
        var http = new StubHandler(request => Json(Embeddings(request.Body.RootElement.GetProperty("input").GetArrayLength())));
        var provider = Voyage(http);

        await provider.EmbedAsync(["class A {}", "class B {}"], EmbeddingPurpose.Document);
        await provider.EmbedAsync(["where is A?"], EmbeddingPurpose.Query);

        var document = http.Requests[0];
        Assert.Equal("/v1/embeddings", document.Path);
        Assert.Equal("Bearer voyage-key", document.Headers["Authorization"]);
        var body = document.Body.RootElement;
        Assert.Equal("voyage-code-3", body.GetProperty("model").GetString());
        Assert.Equal("document", body.GetProperty("input_type").GetString());
        Assert.Equal(SourceChunk.EmbeddingDimensions, body.GetProperty("output_dimension").GetInt32());
        Assert.False(body.GetProperty("truncation").GetBoolean());
        Assert.Equal("query", http.Requests[1].Body.RootElement.GetProperty("input_type").GetString());
    }

    [Fact]
    public async Task Voyage_vectors_are_returned_in_input_order_even_if_the_response_is_not()
    {
        var http = new StubHandler(_ => Json(Embeddings(3, reverse: true)));

        var vectors = await Voyage(http).EmbedAsync(["a", "b", "c"], EmbeddingPurpose.Document);

        Assert.Equal([0f, 1f, 2f], vectors.Select(vector => vector[0]));
        Assert.All(vectors, vector => Assert.Equal(SourceChunk.EmbeddingDimensions, vector.Length));
    }

    [Fact]
    public async Task Voyage_large_input_sets_are_split_to_stay_under_the_request_limits()
    {
        var http = new StubHandler(request => Json(Embeddings(request.Body.RootElement.GetProperty("input").GetArrayLength())));
        var options = FastVoyageOptions();
        options.MaxEstimatedTokensPerRequest = 1_000;
        options.MaxInputsPerRequest = 4;
        var texts = Enumerable.Range(0, 10).Select(i => new string('x', i < 3 ? 1_500 : 10)).ToList();

        var vectors = await Voyage(http, options).EmbedAsync(texts, EmbeddingPurpose.Document);

        Assert.Equal(10, vectors.Count);
        Assert.Equal([1, 1, 4, 4], http.Requests.Select(r => r.Body.RootElement.GetProperty("input").GetArrayLength()));
    }

    [Fact]
    public async Task Voyage_rate_limit_and_server_errors_are_retried()
    {
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.OK]);
        var http = new StubHandler(_ => responses.Dequeue() is var status && status == HttpStatusCode.OK
            ? Json(Embeddings(1))
            : Json("""{"detail":"slow down"}""", status));

        var vectors = await Voyage(http).EmbedAsync(["a"], EmbeddingPurpose.Document);

        Assert.Single(vectors);
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task Voyage_gives_up_after_its_attempt_limit_without_leaking_the_response_or_key()
    {
        var http = new StubHandler(_ => Json("""{"detail":"echo of input: SECRET-SOURCE"}""", HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<EmbeddingProviderException>(
            () => Voyage(http).EmbedAsync(["a"], EmbeddingPurpose.Document));

        Assert.Equal(FastVoyageOptions().MaxAttempts, http.Requests.Count);
        Assert.DoesNotContain("SECRET-SOURCE", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("voyage-key", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Voyage_client_errors_are_not_retried(HttpStatusCode status)
    {
        var http = new StubHandler(_ => Json("""{"detail":"bad"}""", status));

        await Assert.ThrowsAsync<EmbeddingProviderException>(() => Voyage(http).EmbedAsync(["a"], EmbeddingPurpose.Document));

        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData(2, 1024)]
    [InlineData(1, 256)]
    public async Task Voyage_response_with_the_wrong_count_or_dimension_is_rejected(int count, int dimensions)
    {
        var http = new StubHandler(_ => Json(Embeddings(count, dimensions: dimensions)));

        await Assert.ThrowsAsync<EmbeddingProviderException>(() => Voyage(http).EmbedAsync(["a"], EmbeddingPurpose.Document));
    }

    private static AnthropicChatModel Claude(StubHandler handler) => new(
        new AnthropicClient { ApiKey = "sk-ant-api03-test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 },
        new AnthropicChatOptions());

    private static VoyageEmbeddingProvider Voyage(StubHandler handler, VoyageOptions? options = null) =>
        new(new HttpClient(handler), "voyage-key", options ?? FastVoyageOptions(), TimeProvider.System);

    private static VoyageOptions FastVoyageOptions() => new()
    {
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        RetryMaxDelay = TimeSpan.FromMilliseconds(5),
    };

    private static string Embeddings(int count, bool reverse = false, int dimensions = SourceChunk.EmbeddingDimensions)
    {
        var items = Enumerable.Range(0, count).Select(index =>
        {
            var vector = new float[dimensions];
            vector[0] = index;
            return new { @object = "embedding", embedding = vector, index };
        });

        return JsonSerializer.Serialize(new
        {
            @object = "list",
            data = reverse ? items.Reverse() : items,
            model = "voyage-code-3",
            usage = new { total_tokens = 8 },
        });
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record CapturedRequest(string Path, Dictionary<string, string> Headers, JsonDocument Body);

    private sealed class StubHandler(Func<CapturedRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.RequestUri!.AbsolutePath,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)));
            Requests.Add(captured);
            return respond(captured);
        }
    }
}
