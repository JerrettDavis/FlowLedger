using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Abstractions;

/// <summary>A value an open-data / appraiser feed proposes for an asset.</summary>
public sealed record AssetFeedQuote(decimal Amount, DateOnly AsOf, string? Note);

/// <summary>
/// A source of asset valuations (model-based estimate, an appraiser or price-guide API…).
/// Implementations must never transmit <see cref="Asset.SerialNumber"/>; only descriptive fields
/// (category, manufacturer, model, year, opted-in attributes) may leave the system.
/// </summary>
public interface IAssetValuationFeed
{
    /// <summary>Stable feed name, used as the valuation <c>Source</c> and the <c>feed</c> query value.</summary>
    string Name { get; }

    /// <summary>Whether the feed is configured and usable.</summary>
    bool IsEnabled { get; }

    /// <summary>Returns a quote, or null when the feed has no value for this asset.</summary>
    Task<AssetFeedQuote?> GetQuoteAsync(Asset asset, DateOnly asOf, CancellationToken ct = default);
}
