namespace Trace.Application.Indexing;

/// <summary>
/// Provider-neutral text embeddings. Implementations must return one vector of
/// <see cref="Domain.SourceChunk.EmbeddingDimensions"/> values per input, in input order.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Recorded on each snapshot; vectors from different models are never mixed.</summary>
    string ModelId { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default);
}
