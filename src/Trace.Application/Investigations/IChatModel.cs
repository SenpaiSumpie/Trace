namespace Trace.Application.Investigations;

public sealed record ToolDefinition(string Name, string Description, string InputSchemaJson);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public abstract record ModelMessage;

public sealed record UserMessage(string Text) : ModelMessage;

public sealed record AssistantToolCallsMessage(IReadOnlyList<ToolCall> Calls) : ModelMessage;

public sealed record ToolResultMessage(string CallId, string ToolName, string Content, bool IsError) : ModelMessage;

public sealed record ModelRequest(
    string SystemPrompt,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools);

/// <summary>One model turn: either tool calls to run, or the final answer text.</summary>
public sealed record ModelTurn(IReadOnlyList<ToolCall> ToolCalls, string? FinalText)
{
    public static ModelTurn Final(string text) => new([], text);

    public static ModelTurn Call(params ToolCall[] calls) => new(calls, FinalText: null);
}

/// <summary>
/// Provider-neutral chat model with tool calling. Implementations translate to a provider API;
/// they never execute tools themselves and are never given credentials or repository access.
/// </summary>
public interface IChatModel
{
    /// <summary>Recorded on each investigation for reproducibility.</summary>
    string ModelId { get; }

    Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The chat model could not be reached or returned a provider error.</summary>
public sealed class ChatModelException(string message, Exception? innerException = null)
    : Exception(message, innerException);
