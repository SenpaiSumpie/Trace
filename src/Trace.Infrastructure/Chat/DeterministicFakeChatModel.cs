using System.Text.Json;
using Trace.Application.Investigations;

namespace Trace.Infrastructure.Chat;

/// <summary>
/// Test and local-demo stand-in for a language model. It is purely extractive: it searches once
/// with the question's own words and "answers" by pointing at the passages that matched them.
/// It does not understand the question or the code, cannot judge relevance, and says so in its
/// answer. It is refused in the Production environment.
/// </summary>
public sealed class DeterministicFakeChatModel : IChatModel
{
    public const string ProviderName = "deterministic-fake";

    private const int MaxCitedPassages = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ModelId => "fake-extractive-v1";

    public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var question = request.Messages.OfType<UserMessage>().First().Text;
        var searchResult = request.Messages.OfType<ToolResultMessage>().LastOrDefault();

        if (searchResult is null)
        {
            var arguments = JsonSerializer.Serialize(new { query = question, limit = 5 }, Json);
            return Task.FromResult(ModelTurn.Call(new ToolCall("call-1", QuestionPrompt.SearchCode, arguments)));
        }

        // Only passages that share words with the question count as a match.
        var matches = searchResult.IsError
            ? []
            : (JsonSerializer.Deserialize<SearchOutput>(searchResult.Content, Json)?.Hits ?? [])
                .Where(hit => hit.LexicalRank is not null)
                .Take(MaxCitedPassages)
                .ToList();

        var answer = matches.Count == 0
            ? new
            {
                sufficientEvidence = false,
                answer = string.Empty,
                citations = Array.Empty<object>(),
                unknowns = new[] { "No indexed passage shares any words with the question." },
            }
            : new
            {
                sufficientEvidence = true,
                answer = "Extractive test model — no language model interpreted this question. The indexed "
                    + $"passages that best match its wording are listed as evidence, starting with {matches[0].Path} "
                    + $"lines {matches[0].StartLine}-{matches[0].EndLine}.",
                citations = matches
                    .Select(hit => (object)new
                    {
                        path = hit.Path,
                        startLine = hit.StartLine,
                        endLine = hit.EndLine,
                        supports = "Matched the wording of the question.",
                    })
                    .ToArray(),
                unknowns = new[] { "Whether these passages actually answer the question was not assessed." },
            };

        return Task.FromResult(ModelTurn.Final(JsonSerializer.Serialize(answer, Json)));
    }

    private sealed record SearchOutput(IReadOnlyList<SearchHit>? Hits);

    private sealed record SearchHit(string Path, int StartLine, int EndLine, int? LexicalRank);
}
