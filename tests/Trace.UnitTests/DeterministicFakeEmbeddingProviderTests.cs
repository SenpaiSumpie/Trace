using Trace.Domain;
using Trace.Infrastructure.Embeddings;

namespace Trace.UnitTests;

public class DeterministicFakeEmbeddingProviderTests
{
    private readonly DeterministicFakeEmbeddingProvider provider = new();

    [Fact]
    public async Task Returns_one_unit_vector_of_the_stored_dimension_per_input_in_order()
    {
        var vectors = await provider.EmbedAsync(["alpha beta", "gamma", "alpha beta"]);

        Assert.Equal(3, vectors.Count);
        Assert.All(vectors, vector =>
        {
            Assert.Equal(SourceChunk.EmbeddingDimensions, vector.Length);
            Assert.Equal(1.0, Math.Sqrt(vector.Sum(value => (double)value * value)), precision: 5);
        });
        Assert.Equal(vectors[0], vectors[2]);
        Assert.NotEqual(vectors[0], vectors[1]);
    }

    [Fact]
    public async Task Vectors_are_stable_across_processes_and_platforms()
    {
        var vector = (await provider.EmbedAsync(["page"]))[0];

        // FNV-1a("page") = 0x815DFA76: bucket 0x815DFA76 % 1024 = 630, sign bit set => -1.
        var nonZero = Assert.Single(vector.Index(), entry => entry.Item != 0f);
        Assert.Equal((630, -1f), nonZero);
    }

    [Fact]
    public async Task Texts_sharing_words_are_closer_than_unrelated_texts()
    {
        var vectors = await provider.EmbedAsync(
        [
            "var skip = request.Page * request.PageSize;",
            "how is the page size used when listing a page of tasks",
            "rejects requests that do not carry the configured api key",
        ]);

        Assert.True(Cosine(vectors[0], vectors[1]) > Cosine(vectors[0], vectors[2]));
    }

    [Fact]
    public async Task Identifier_casing_and_separators_do_not_matter()
    {
        var vectors = await provider.EmbedAsync(["PageSize", "page_size", "pageSize", "PAGE SIZE"]);

        Assert.All(vectors, vector => Assert.Equal(vectors[0], vector));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}();")]
    public async Task Text_without_words_still_yields_a_unit_vector(string text)
    {
        var vector = (await provider.EmbedAsync([text]))[0];

        Assert.Equal(1f, vector.Sum(value => value * value));
    }

    private static float Cosine(float[] left, float[] right) => left.Zip(right, (a, b) => a * b).Sum();
}
