using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Trace.Application.Investigations;

namespace Trace.Infrastructure.Chat;

public sealed class AnthropicChatOptions
{
    public const string DefaultModel = "claude-opus-5-5";

    public string Model { get; set; } = DefaultModel;

    /// <summary>
    /// How much effort the model spends (thinking depth and thoroughness). Set explicitly
    /// because the default differs between models.
    /// </summary>
    public Effort Effort { get; set; } = Effort.Medium;

    /// <summary>Per-response output cap. Kept modest so non-streaming requests stay within HTTP timeouts.</summary>
    public long MaxOutputTokens { get; set; } = 16_000;
}

/// <summary>
/// Claude through the Anthropic Messages API. Translates the provider-neutral conversation to
/// Messages API blocks and back; it never executes a tool. The assistant turn is kept exactly
/// as returned (including thinking blocks and their signatures) and replayed unchanged on the
/// next request, as the API requires.
/// </summary>
public sealed class AnthropicChatModel(AnthropicClient client, AnthropicChatOptions options) : IChatModel
{
    public const string ProviderName = "anthropic";

    public string ModelId => options.Model;

    public async Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxOutputTokens,
            System = request.SystemPrompt,
            OutputConfig = new OutputConfig { Effort = options.Effort },
            Tools = [.. request.Tools.Select(ToTool)],
            Messages = ToMessages(request.Messages),

            // The system prompt, tools and earlier turns are identical on every request of an
            // investigation; let the API cache that prefix.
            CacheControl = new CacheControlEphemeral(),
        };

        Message response;
        try
        {
            response = await client.Messages.Create(parameters, cancellationToken);
        }
        catch (AnthropicApiException exception)
        {
            // The SDK has already retried rate limits and server errors. Keep the provider's
            // message out of ours: it is logged with the job, not shown to users.
            throw new ChatModelException(
                $"The Anthropic API request failed ({exception.GetType().Name}).", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ChatModelException("The Anthropic API could not be reached.", exception);
        }

        var cacheRead = response.Usage.CacheReadInputTokens ?? 0;
        var cacheWrite = response.Usage.CacheCreationInputTokens ?? 0;
        var usage = new ModelUsage(
            response.Usage.InputTokens + cacheWrite + cacheRead,
            response.Usage.OutputTokens,
            cacheRead,
            cacheWrite);

        // Always check the stop reason before trusting the content.
        if (response.StopReason == StopReason.Refusal)
        {
            throw new ChatModelRefusedException("The model declined to respond to this request.");
        }

        if (response.StopReason == StopReason.MaxTokens)
        {
            throw new ChatModelException("The model's response was cut off at the output limit.");
        }

        List<ContentBlockParam> assistantTurn = [];
        List<ToolCall> toolCalls = [];
        List<string> texts = [];
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out TextBlock? text))
            {
                assistantTurn.Add(new TextBlockParam { Text = text.Text });
                texts.Add(text.Text);
            }
            else if (block.TryPickThinking(out ThinkingBlock? thinking))
            {
                assistantTurn.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
            }
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
            {
                assistantTurn.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
            }
            else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
            {
                assistantTurn.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                toolCalls.Add(new ToolCall(toolUse.ID, toolUse.Name, JsonSerializer.Serialize(toolUse.Input)));
            }
        }

        return new ModelTurn(
            toolCalls,
            toolCalls.Count == 0 ? string.Concat(texts) : null,
            ProviderState: assistantTurn,
            usage);
    }

    private static ToolUnion ToTool(ToolDefinition definition)
    {
        using var schema = JsonDocument.Parse(definition.InputSchemaJson);
        var root = schema.RootElement;

        return new Tool
        {
            Name = definition.Name,
            Description = definition.Description,
            InputSchema = new()
            {
                Properties = root.GetProperty("properties").EnumerateObject()
                    .ToDictionary(property => property.Name, property => property.Value.Clone()),
                Required = root.TryGetProperty("required", out var required)
                    ? [.. required.EnumerateArray().Select(name => name.GetString()!)]
                    : [],
            },
        };
    }

    private static List<MessageParam> ToMessages(IReadOnlyList<ModelMessage> messages)
    {
        List<MessageParam> converted = [];
        List<ContentBlockParam> pendingToolResults = [];

        void FlushToolResults()
        {
            if (pendingToolResults.Count > 0)
            {
                // Every result of one assistant turn goes back in a single user message.
                converted.Add(new UserMessageParam { Content = pendingToolResults });
                pendingToolResults = [];
            }
        }

        foreach (var message in messages)
        {
            switch (message)
            {
                case ToolResultMessage result:
                    pendingToolResults.Add(new ToolResultBlockParam
                    {
                        ToolUseID = result.CallId,
                        Content = result.Content,
                        IsError = result.IsError,
                    });
                    break;

                case UserMessage user:
                    FlushToolResults();
                    converted.Add(new UserMessageParam { Content = user.Text });
                    break;

                case AssistantToolCallsMessage assistant:
                    FlushToolResults();
                    converted.Add(new AssistantMessageParam
                    {
                        Content = assistant.ProviderState as List<ContentBlockParam> ?? Reconstruct(assistant),
                    });
                    break;

                default:
                    throw new NotSupportedException($"Unsupported message type {message.GetType().Name}.");
            }
        }

        FlushToolResults();
        return converted;
    }

    /// <summary>Only used when a turn did not come from this adapter (for example in tests).</summary>
    private static List<ContentBlockParam> Reconstruct(AssistantToolCallsMessage assistant) =>
        [.. assistant.Calls.Select(call => (ContentBlockParam)new ToolUseBlockParam
        {
            ID = call.Id,
            Name = call.Name,
            Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.ArgumentsJson) ?? [],
        })];
}
