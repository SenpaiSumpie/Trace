using Trace.Application.Indexing;

namespace Trace.IntegrationTests.Support;

internal static class EmbeddingTestExtensions
{
    /// <summary>Embeds as stored passages; the fake provider treats both purposes alike.</summary>
    public static Task<IReadOnlyList<float[]>> EmbedAsync(this IEmbeddingProvider provider, IReadOnlyList<string> texts) =>
        provider.EmbedAsync(texts, EmbeddingPurpose.Document);
}
