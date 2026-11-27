using System.Globalization;
using System.Text.Json;
using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Assets;

/// <summary>
/// Configuration for the generic JSON appraisal feed. Disabled by default; no vendor is assumed.
/// Bind from <c>AssetFeeds:Http</c>.
/// </summary>
internal sealed class HttpJsonAppraisalFeedOptions
{
    public const string SectionName = "AssetFeeds:Http";

    public bool Enabled { get; set; }

    /// <summary>Feed name shown as the valuation source and used in <c>?feed=</c>.</summary>
    public string Name { get; set; } = "appraiser";

    /// <summary>
    /// Absolute URL with optional placeholders: <c>{category} {manufacturer} {model} {year}</c> and
    /// <c>{attr:FieldName}</c> for an opted-in custom field (e.g. a VIN). Values are URL-escaped.
    /// Serial numbers are intentionally not available as a placeholder.
    /// </summary>
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>Optional header carrying <see cref="ApiKey"/> (never put keys in the URL).</summary>
    public string? ApiKeyHeader { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>Dotted path to the numeric value in the JSON response, e.g. <c>data.estimate.value</c> or <c>items.0.price</c>.</summary>
    public string ValuePath { get; set; } = "value";
}

/// <summary>
/// Pulls a value for an asset from any HTTP JSON endpoint described by configuration. Only descriptive
/// fields ever leave the system — never the serial number.
/// </summary>
internal sealed partial class HttpJsonAppraisalFeed(
    IHttpClientFactory httpFactory,
    IOptions<HttpJsonAppraisalFeedOptions> options,
    ILogger<HttpJsonAppraisalFeed> logger) : IAssetValuationFeed
{
    public const string HttpClientName = "asset-appraisal-feed";

    private readonly HttpJsonAppraisalFeedOptions _o = options.Value;

    public string Name => _o.Name;

    public bool IsEnabled => _o.Enabled && Uri.TryCreate(_o.UrlTemplate, UriKind.Absolute, out _);

    public async Task<AssetFeedQuote?> GetQuoteAsync(Asset asset, DateOnly asOf, CancellationToken ct = default)
    {
        var url = BuildUrl(_o.UrlTemplate, asset);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
        {
            LogAssetFeedSkippedURLTemplate(logger, Name);
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(_o.ApiKeyHeader) && !string.IsNullOrWhiteSpace(_o.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(_o.ApiKeyHeader, _o.ApiKey);
        }

        try
        {
            using var response = await httpFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogAssetFeedReturnedHTTP(logger, Name, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var amount = ReadDecimal(doc.RootElement, _o.ValuePath);
            return amount is >= 0m ? new AssetFeedQuote(amount.Value, asOf, $"Fetched from {Name}") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // A flaky or non-JSON endpoint means "no value", not a server error.
            LogAssetFeedFailed(logger, ex, Name);
            return null;
        }
    }

    internal static string BuildUrl(string template, Asset asset)
    {
        var url = template
            .Replace("{category}", Uri.EscapeDataString(asset.Category.ToString()), StringComparison.OrdinalIgnoreCase)
            .Replace("{manufacturer}", Uri.EscapeDataString(asset.Manufacturer ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{model}", Uri.EscapeDataString(asset.Model ?? string.Empty), StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", asset.PurchaseDate?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        foreach (var (key, value) in asset.Attributes)
        {
            url = url.Replace($"{{attr:{key}}}", Uri.EscapeDataString(value), StringComparison.OrdinalIgnoreCase);
        }

        return url;
    }

    internal static decimal? ReadDecimal(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index)
                && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next))
            {
                current = next;
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.Number when current.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(current.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asset feed {Feed} skipped: URL template must resolve to an https URL.")]
    private static partial void LogAssetFeedSkippedURLTemplate(ILogger logger, string? feed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asset feed {Feed} returned HTTP {Status}")]
    private static partial void LogAssetFeedReturnedHTTP(ILogger logger, string? feed, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Asset feed {Feed} failed")]
    private static partial void LogAssetFeedFailed(ILogger logger, Exception exception, string? feed);
}
