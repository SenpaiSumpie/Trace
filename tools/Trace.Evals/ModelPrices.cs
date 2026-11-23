using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Trace.Evals;

/// <summary>
/// What one model charges, in US dollars per million tokens. Read from configuration
/// (<c>Eval:Prices:&lt;model id&gt;</c>) because prices change and differ per model; nothing in
/// code knows a price.
/// </summary>
public sealed record ModelPrices(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal CacheReadPerMillion,
    decimal CacheWritePerMillion)
{
    public const string Currency = "USD";
    public const string Section = "Eval:Prices";

    private const decimal Million = 1_000_000m;

    /// <summary>
    /// The cost of the given usage. <paramref name="inputTokens"/> is every input token including
    /// the two cached kinds (as the pipeline records it); cached tokens are charged at their own
    /// rates and the rest at the plain input rate.
    /// </summary>
    public decimal Estimate(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens)
    {
        var plainInput = Math.Max(0, inputTokens - cacheReadTokens - cacheWriteTokens);
        var cost = ((plainInput * InputPerMillion)
            + (outputTokens * OutputPerMillion)
            + (cacheReadTokens * CacheReadPerMillion)
            + (cacheWriteTokens * CacheWritePerMillion)) / Million;
        return Math.Round(cost, 6);
    }

    /// <summary>
    /// The configured prices for a model, or null if none are configured (the cost is then not
    /// estimated). A partly configured or invalid entry is an error rather than a silent zero.
    /// </summary>
    public static ModelPrices? FromConfiguration(IConfiguration configuration, string modelId)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection($"{Section}:{modelId}");
        if (!section.Exists())
        {
            return null;
        }

        return new ModelPrices(
            Price(section, nameof(InputPerMillion)),
            Price(section, nameof(OutputPerMillion)),
            Price(section, nameof(CacheReadPerMillion)),
            Price(section, nameof(CacheWritePerMillion)));
    }

    private static decimal Price(IConfigurationSection section, string key) =>
        decimal.TryParse(section[key], NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price >= 0
            ? price
            : throw new InvalidOperationException(
                $"'{section.Path}:{key}' must be a non-negative number (US dollars per million tokens) but was '{section[key]}'.");
}
