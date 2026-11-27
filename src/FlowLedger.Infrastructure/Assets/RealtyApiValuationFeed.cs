using System.Text.Json;
using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Assets;

/// <summary>Configuration for the RealtyAPI.io (Redfin) home-value feed. Bind from <c>RealtyApi</c>.</summary>
internal sealed class RealtyApiOptions
{
    public const string SectionName = "RealtyApi";

    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string BaseUrl { get; set; } = "https://redfin.realtyapi.io";

    /// <summary>Asset attribute holding the street address ("street, city, ST zip").</summary>
    public string AddressAttribute { get; set; } = "Address";
}

/// <summary>
/// Real-estate value from the Redfin Estimate via RealtyAPI.io. One <c>/detailsbyaddress</c> call per
/// property (the response embeds <c>details.avm.__root.avmInfo.predictedValue</c>). Only the address leaves
/// the system, never the serial number.
/// </summary>
internal sealed partial class RealtyApiValuationFeed(
    IHttpClientFactory httpFactory,
    IOptions<RealtyApiOptions> options,
    ILogger<RealtyApiValuationFeed> logger) : IAssetValuationFeed
{
    public const string FeedName = "realtyapi";
    public const string HttpClientName = "realtyapi-feed";

    private readonly RealtyApiOptions _o = options.Value;

    public string Name => FeedName;

    public bool IsEnabled => _o.Enabled && !string.IsNullOrWhiteSpace(_o.ApiKey)
                             && Uri.TryCreate(_o.BaseUrl, UriKind.Absolute, out _);

    public async Task<AssetFeedQuote?> GetQuoteAsync(Asset asset, DateOnly asOf, CancellationToken ct = default)
    {
        if (asset.Category != AssetCategory.RealEstate)
        {
            return null;
        }

        var address = asset.Attributes
            .FirstOrDefault(kv => string.Equals(kv.Key, _o.AddressAttribute, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(address))
        {
            LogRealtyAPISkippedAssetHasNo(logger, _o.AddressAttribute);
            return null;
        }

        var uri = new Uri($"{_o.BaseUrl.TrimEnd('/')}/detailsbyaddress?property_address={Uri.EscapeDataString(address)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("x-realtyapi-key", _o.ApiKey);

        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogRealtyAPIReturnedHTTP(logger, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var amount = HttpJsonAppraisalFeed.ReadDecimal(doc.RootElement, "details.avm.__root.avmInfo.predictedValue");
            return amount is > 0m ? new AssetFeedQuote(FlowLedger.Domain.ValueObjects.MoneyRounding.Round(amount.Value, 0), asOf, "Redfin Estimate via RealtyAPI") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogRealtyAPIFeedFailed(logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "RealtyAPI skipped: asset has no '{Attr}' attribute.")]
    private static partial void LogRealtyAPISkippedAssetHasNo(ILogger logger, string? attr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RealtyAPI returned HTTP {Status}")]
    private static partial void LogRealtyAPIReturnedHTTP(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RealtyAPI feed failed")]
    private static partial void LogRealtyAPIFeedFailed(ILogger logger, Exception exception);
}
