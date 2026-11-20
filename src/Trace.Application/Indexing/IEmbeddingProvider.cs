namespace Trace.Application.Indexing;

/// <summary>What a text is being embedded for. Retrieval models embed stored passages and search queries differently.</summary>
public enum EmbeddingPurpose
{
    Document,
    Query,
}

/// <summary>
/// Provider-neutral text embeddings. Implementations must return one vector of
/// <see cref="Domain.SourceChunk.EmbeddingDimensions"/> values per input, in input order.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Recorded on each snapshot; vectors from different models are never mixed.</summary>
    string ModelId { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default);
}
