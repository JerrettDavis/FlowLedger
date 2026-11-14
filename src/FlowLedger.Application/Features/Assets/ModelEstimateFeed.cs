using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;

namespace FlowLedger.Application.Features.Assets;

/// <summary>Network-free feed that projects the asset's own depreciation/appreciation model forward.</summary>
public sealed class ModelEstimateFeed : IAssetValuationFeed
{
    public const string FeedName = "model";

    public string Name => FeedName;
    public bool IsEnabled => true;

    public Task<AssetFeedQuote?> GetQuoteAsync(Asset asset, DateOnly asOf, CancellationToken ct = default)
    {
        if (asset.Method == ValuationMethod.Manual)
        {
            return Task.FromResult<AssetFeedQuote?>(null);
        }

        var estimate = AssetValuationEstimator.Estimate(asset, asOf);
        return Task.FromResult<AssetFeedQuote?>(
            new AssetFeedQuote(estimate.Amount, asOf, $"{asset.Method} model estimate"));
    }
}
