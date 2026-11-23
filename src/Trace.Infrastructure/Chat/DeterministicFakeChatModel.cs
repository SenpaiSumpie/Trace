using System.Text.Json;
using Trace.Application.Investigations;
using Trace.Application.Plans;

namespace Trace.Infrastructure.Chat;

/// <summary>
/// Test and local-demo stand-in for a language model. It is purely extractive: it searches once
/// with the question's own words and "answers" by pointing at the passages that matched them.
/// It does not understand the question or the code, cannot judge relevance, and says so in its
/// answer. It is refused in the Production environment.
/// <para>
/// Asked for a plan (the request carries the plan prompt), it does the same with the issue's
/// title: one search, then a plan whose every entry points at a passage that search returned in
/// full. It reads the issue the way a model must, from between the markers of the first
/// message, and its proposal goes through the same validation as any model's.
/// </para>
/// </summary>
public sealed class DeterministicFakeChatModel : IChatModel
{
    public const string ProviderName = "deterministic-fake";

    private const int MaxCitedPassages = 3;
    private const int MaxQueryLength = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ModelId => "fake-extractive-v1";

    public Task<ModelTurn> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var firstMessage = request.Messages.OfType<UserMessage>().First().Text;
        var searchResult = request.Messages.OfType<ToolResultMessage>().LastOrDefault();
        var planning = request.SystemPrompt == PlanPrompt.System;

        if (searchResult is null)
        {
            var query = planning ? PlanQuery(PlanPrompt.TryReadIssue(firstMessage)) : firstMessage;
            var arguments = JsonSerializer.Serialize(new { query, limit = 5 }, Json);
            return Task.FromResult(ModelTurn.Call(new ToolCall("call-1", QuestionPrompt.SearchCode, arguments)));
        }

        // Only passages that share words with the question count as a match.
        var matches = searchResult.IsError
            ? []
            : (JsonSerializer.Deserialize<SearchOutput>(searchResult.Content, Json)?.Hits ?? [])
                .Where(hit => hit.LexicalRank is not null)
                .Take(MaxCitedPassages)
                .ToList();

        return Task.FromResult(ModelTurn.Final(planning ? Plan(matches) : Answer(matches)));
    }

    private static string Answer(List<SearchHit> matches)
    {
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

        return JsonSerializer.Serialize(answer, Json);
    }

    /// <summary>The issue's title is the search; its body when it has no title.</summary>
    private static string PlanQuery(QuotedIssue? issue)
    {
        var text = issue is null
            ? string.Empty
            : string.IsNullOrWhiteSpace(issue.Title) ? issue.Body : issue.Title;
        var query = new string([.. text.Select(character => char.IsControl(character) ? ' ' : character)]).Trim();
        return query.Length <= MaxQueryLength ? query : query[..MaxQueryLength];
    }

    /// <summary>
    /// A plan made only of what the search returned. Passages that came back as a preview or
    /// were shown earlier are left out: their text was not in this result, so a model may not
    /// cite them from it.
    /// </summary>
    private static string Plan(List<SearchHit> matches)
    {
        var shown = matches.Where(hit => hit.Text is not null).ToList();
        if (shown.Count == 0)
        {
            return JsonSerializer.Serialize(
                new
                {
                    sufficientEvidence = false,
                    summary = (object?)null,
                    steps = Array.Empty<object>(),
                    filesToChange = Array.Empty<object>(),
                    risks = Array.Empty<object>(),
                    verification = Array.Empty<object>(),
                    unknowns = new[] { Statement("No indexed passage shares any words with the title of the issue.") },
                },
                Json);
        }

        var first = shown[0];
        return JsonSerializer.Serialize(
            new
            {
                sufficientEvidence = true,
                summary = Statement(
                    "Extractive test model — no language model read this issue. The steps point at the indexed "
                    + "passages that best match the wording of the issue's title; they are places to look, not a design.",
                    first),
                steps = shown
                    .Select(hit => Statement(
                        $"Review {hit.Path} lines {hit.StartLine}-{hit.EndLine}, which match the wording of the issue's title, and decide what has to change there.",
                        hit))
                    .ToArray(),
                filesToChange = shown
                    .GroupBy(hit => hit.Path, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        path = group.Key,
                        change = ModelPlan.Modify,
                        reason = "A passage of this file matches the wording of the issue's title.",
                        citations = new[] { Location(group.First()) },
                    })
                    .ToArray(),
                risks = new[]
                {
                    Statement("The passage was chosen by matching words only, so the change may belong somewhere else.", first),
                },
                verification = new[]
                {
                    Statement($"Re-read {first.Path} lines {first.StartLine}-{first.EndLine} after the change and run the tests that cover it.", first),
                },
                unknowns = new[] { Statement("Whether these passages are the right place for the change was not assessed.") },
            },
            Json);
    }

    private static object Statement(string text, params SearchHit[] cited) =>
        new { text, citations = cited.Select(Location).ToArray() };

    private static object Location(SearchHit hit) =>
        new { path = hit.Path, startLine = hit.StartLine, endLine = hit.EndLine };

    private sealed record SearchOutput(IReadOnlyList<SearchHit>? Hits);

    private sealed record SearchHit(string Path, int StartLine, int EndLine, int? LexicalRank, string? Text = null);
}
