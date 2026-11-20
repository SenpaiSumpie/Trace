using System.Text.RegularExpressions;
using Trace.Application.Indexing;
using Trace.Domain;

namespace Trace.Infrastructure.Embeddings;

/// <summary>
/// Test and local-demo embeddings: a hashed bag of words. Identical text always yields the
/// identical vector on every machine, and texts that share words are closer than texts that
/// do not. It has no semantic understanding and is refused in the Production environment.
/// </summary>
public sealed partial class DeterministicFakeEmbeddingProvider : IEmbeddingProvider
{
    public const string ProviderName = "deterministic-fake";

    public string ModelId => "fake-hash-v1";

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(Embed)]);
    }

    private static float[] Embed(string text)
    {
        var vector = new float[SourceChunk.EmbeddingDimensions];
        foreach (var match in Word().EnumerateMatches(text))
        {
            var hash = Fnv1a(text.AsSpan(match.Index, match.Length));
            var bucket = (int)(hash % (uint)vector.Length);
            vector[bucket] += (hash & 0x8000_0000) == 0 ? 1f : -1f;
        }

        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm == 0f)
        {
            // No words (or they cancelled out): still return a unit vector so cosine distance is defined.
            vector[0] = 1f;
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    /// <summary>Case-insensitive FNV-1a. Unlike <see cref="string.GetHashCode()"/>, stable across processes.</summary>
    private static uint Fnv1a(ReadOnlySpan<char> word)
    {
        var hash = 2166136261;
        foreach (var character in word)
        {
            hash = (hash ^ char.ToLowerInvariant(character)) * 16777619;
        }

        return hash;
    }

    // Splits identifiers into words: "PageRequest" -> Page, Request; "page_size" -> page, size.
    [GeneratedRegex("[A-Z]?[a-z]+|[A-Z]+(?![a-z])|[0-9]+")]
    private static partial Regex Word();
}
