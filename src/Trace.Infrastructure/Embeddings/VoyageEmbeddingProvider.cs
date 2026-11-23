using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Trace.Application.Indexing;
using Trace.Domain;

namespace Trace.Infrastructure.Embeddings;

public sealed class VoyageOptions
{
    public const string DefaultModel = "voyage-code-3";

    public string Model { get; set; } = DefaultModel;

    public Uri Endpoint { get; set; } = new("https://api.voyageai.com/v1/embeddings");

    /// <summary>Voyage accepts at most 120,000 tokens per request for this model; stay well under it.</summary>
    public int MaxEstimatedTokensPerRequest { get; set; } = 80_000;

    public int MaxInputsPerRequest { get; set; } = 128;

    public int MaxAttempts { get; set; } = 4;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>The embedding API failed in a way retrying did not fix.</summary>
public sealed class EmbeddingProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Voyage AI embeddings over HTTP. Stored passages and search queries are embedded with their
/// respective input type. Inputs are never truncated silently: an over-long input is an error.
/// </summary>
public sealed partial class VoyageEmbeddingProvider(
    HttpClient http,
    string apiKey,
    VoyageOptions options,
    TimeProvider timeProvider,
    ILogger<VoyageEmbeddingProvider>? logger = null)
    : IEmbeddingProvider
{
    private const int MaxReasonLength = 300;

    public const string ProviderName = "voyage";

    public string ModelId => options.Model;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var embeddings = new List<float[]>(texts.Count);
        foreach (var batch in SplitIntoRequests(texts))
        {
            embeddings.AddRange(await EmbedBatchAsync(batch, purpose, cancellationToken));
        }

        return embeddings;
    }

    /// <summary>Groups inputs so each request stays under the provider's input and token limits.</summary>
    private IEnumerable<List<string>> SplitIntoRequests(IReadOnlyList<string> texts)
    {
        var batch = new List<string>();
        var estimatedTokens = 0;
        foreach (var text in texts)
        {
            // Source code averages well over 2 characters per token; 2 is a deliberately high estimate.
            var tokens = (text.Length / 2) + 1;
            if (batch.Count > 0
                && (batch.Count >= options.MaxInputsPerRequest || estimatedTokens + tokens > options.MaxEstimatedTokensPerRequest))
            {
                yield return batch;
                batch = [];
                estimatedTokens = 0;
            }

            batch.Add(text);
            estimatedTokens += tokens;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        List<string> batch,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        var body = new EmbeddingRequest(
            batch,
            options.Model,
            purpose == EmbeddingPurpose.Query ? "query" : "document",
            SourceChunk.EmbeddingDimensions,
            Truncation: false);

        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await ReadEmbeddingsAsync(response, batch.Count, cancellationToken);
                }

                if (attempt == 1)
                {
                    await LogRefusalReasonAsync(response, cancellationToken);
                }

                if (!IsTransient(response.StatusCode) || attempt >= options.MaxAttempts)
                {
                    // The response body is not included: it can echo the input.
                    throw new EmbeddingProviderException(
                        $"The embedding API returned {(int)response.StatusCode} after {attempt} attempt(s).");
                }

                await Task.Delay(RetryDelay(attempt, response.Headers.RetryAfter), timeProvider, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                if (attempt >= options.MaxAttempts)
                {
                    throw new EmbeddingProviderException(
                        $"The embedding API could not be reached after {attempt} attempt(s).", exception);
                }

                await Task.Delay(RetryDelay(attempt, retryAfter: null), timeProvider, cancellationToken);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private static async Task<IReadOnlyList<float[]>> ReadEmbeddingsAsync(
        HttpResponseMessage response,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
        var data = payload?.Data ?? [];
        if (data.Count != expectedCount
            || data.Any(item => item.Embedding is null || item.Embedding.Length != SourceChunk.EmbeddingDimensions)
            || data.Select(item => item.Index).Distinct().Count() != expectedCount)
        {
            throw new EmbeddingProviderException(
                $"The embedding API did not return {expectedCount} vectors of {SourceChunk.EmbeddingDimensions} dimensions.");
        }

        return [.. data.OrderBy(item => item.Index).Select(item => item.Embedding!)];
    }

    /// <summary>
    /// Logs the provider's own explanation when it refuses a request because of the key or
    /// the account (401, 403, 429): those say what to fix, such as a missing payment method.
    /// Bodies of other errors are never read, because they can echo the source text sent.
    /// </summary>
    private async Task LogRefusalReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (logger is null
            || response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return;
        }

        string? reason = null;
        try
        {
            reason = (await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken))?.Detail;
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or NotSupportedException)
        {
            // No readable explanation; the status code alone is logged.
        }

        var cleaned = new string([.. (reason ?? string.Empty).Select(character => char.IsControl(character) ? ' ' : character)]).Trim();
        LogRefused(
            logger,
            (int)response.StatusCode,
            cleaned.Length == 0 ? "no reason given" : cleaned.Length <= MaxReasonLength ? cleaned : cleaned[..MaxReasonLength] + "…");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The embedding API refused a request with {Status}: {Reason}")]
    private static partial void LogRefused(ILogger logger, int status, string reason);

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)status >= 500;

    private TimeSpan RetryDelay(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        var backoff = TimeSpan.FromTicks((long)Math.Min(
            options.RetryBaseDelay.Ticks * Math.Pow(2, attempt - 1), options.RetryMaxDelay.Ticks));
        var requested = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - timeProvider.GetUtcNow() : (TimeSpan?)null);

        return requested is { } wait && wait > backoff
            ? TimeSpan.FromTicks(Math.Min(wait.Ticks, options.RetryMaxDelay.Ticks))
            : backoff;
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("output_dimension")] int OutputDimension,
        [property: JsonPropertyName("truncation")] bool Truncation);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] IReadOnlyList<EmbeddingItem>? Data);

    private sealed record ErrorResponse([property: JsonPropertyName("detail")] string? Detail);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("embedding")] float[]? Embedding,
        [property: JsonPropertyName("index")] int Index);
}
