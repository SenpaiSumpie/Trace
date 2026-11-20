using System.Text.Json;
using Trace.Application.Investigations;

namespace Trace.IntegrationTests.Support;

/// <summary>
/// A chat model that plays a fixed script, one entry per turn (the last entry repeats). It lets
/// tests put the orchestrator in front of any model behaviour, including a hostile one.
/// </summary>
internal sealed class ScriptedChatModel(params Func<ModelRequest, ModelTurn>[] turns) : IChatModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private int callNumber;

    public string ModelId => "scripted-v1";

    /// <summary>Every request the orchestrator sent, in order.</summary>
    public List<ModelRequest> Requests { get; } = [];

    public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(turns[Math.Min(Requests.Count - 1, turns.Length - 1)](request));
    }

    public ToolCall Tool(string name, object arguments) =>
        new($"call-{++callNumber}", name, JsonSerializer.Serialize(arguments, Json));

    public ToolCall Search(string query, int? limit = null) =>
        Tool(QuestionPrompt.SearchCode, limit is null ? new { query } : new { query, limit });

    public ToolCall Read(string path, int startLine, int endLine) =>
        Tool(QuestionPrompt.ReadFileRange, new { path, startLine, endLine });

    public static ModelTurn Answer(string answer, params (string Path, int StartLine, int EndLine)[] citations) =>
        ModelTurn.Final(JsonSerializer.Serialize(
            new
            {
                sufficientEvidence = true,
                answer,
                citations = citations.Select(citation => new
                {
                    path = citation.Path,
                    startLine = citation.StartLine,
                    endLine = citation.EndLine,
                    supports = "Shows the behaviour described.",
                }),
                unknowns = new[] { "Runtime behaviour was not observed." },
            },
            Json));

    public static ModelTurn NotEnoughEvidence(string leakedAnswer = "") =>
        ModelTurn.Final(JsonSerializer.Serialize(
            new
            {
                sufficientEvidence = false,
                answer = leakedAnswer,
                citations = Array.Empty<object>(),
                unknowns = new[] { "The repository does not show this." },
            },
            Json));

    /// <summary>The tool results the orchestrator returned for the most recent tool calls.</summary>
    public static IReadOnlyList<ToolResultMessage> LastToolResults(ModelRequest request)
    {
        var lastCalls = request.Messages.OfType<AssistantToolCallsMessage>().Last().Calls.Select(call => call.Id).ToHashSet();
        return [.. request.Messages.OfType<ToolResultMessage>().Where(result => lastCalls.Contains(result.CallId))];
    }
}
