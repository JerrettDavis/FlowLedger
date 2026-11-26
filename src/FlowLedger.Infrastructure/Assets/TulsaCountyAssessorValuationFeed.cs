using System.Net;
using System.Text.RegularExpressions;
using FlowLedger.Application.Abstractions;
using FlowLedger.Domain.Aggregates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Assets;

/// <summary>Configuration for the Tulsa County (OK) Assessor feed. Bind from <c>TulsaAssessor</c>.</summary>
internal sealed class TulsaAssessorOptions
{
    public const string SectionName = "TulsaAssessor";

    /// <summary>Public site, no key needed. Disabled by default so it never calls out unasked.</summary>
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://assessor.tulsacounty.org";

    /// <summary>Asset attribute holding the street address ("123 S Main St, Tulsa, OK 74103").</summary>
    public string AddressAttribute { get; set; } = "Address";

    /// <summary>Optional asset attribute holding the assessor account number (skips the address search).</summary>
    public string AccountAttribute { get; set; } = "AssessorAccountNo";
}

/// <summary>
/// Assessed Fair Cash (market) value from the Tulsa County Assessor's public site. The site has no JSON API, so
/// this reads the server-rendered pages: <c>/Property/SearchAddress</c> to find the account, then
/// <c>/Property/Info?accountNo=</c> for the market value. Only the street address leaves the system.
/// The value is an annual (Jan 1) assessment, not a live estimate.
/// </summary>
internal sealed partial class TulsaCountyAssessorValuationFeed(
    IHttpClientFactory httpFactory,
    IOptions<TulsaAssessorOptions> options,
    ILogger<TulsaCountyAssessorValuationFeed> logger) : IAssetValuationFeed
{
    public const string FeedName = "tulsa-assessor";
    public const string HttpClientName = "tulsa-assessor-feed";

    private readonly TulsaAssessorOptions _o = options.Value;

    public string Name => FeedName;

    public bool IsEnabled => _o.Enabled && Uri.TryCreate(_o.BaseUrl, UriKind.Absolute, out _);

    public async Task<AssetFeedQuote?> GetQuoteAsync(Asset asset, DateOnly asOf, CancellationToken ct = default)
    {
        if (asset.Category != AssetCategory.RealEstate)
        {
            return null;
        }

        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            var account = Attr(asset, _o.AccountAttribute);

            if (string.IsNullOrWhiteSpace(account))
            {
                var parsed = ParseAddress(Attr(asset, _o.AddressAttribute));
                if (parsed is null)
                {
                    logger.LogWarning("Tulsa assessor skipped: asset has no usable '{Attr}' street address.", _o.AddressAttribute);
                    return null;
                }

                var searchUrl = $"{_o.BaseUrl.TrimEnd('/')}/Property/SearchAddress?streetNo={Uri.EscapeDataString(parsed.Value.No)}"
                                + $"&preDir={Uri.EscapeDataString(parsed.Value.PreDir)}&streetName={Uri.EscapeDataString(parsed.Value.Name)}"
                                + $"&streetType={Uri.EscapeDataString(parsed.Value.Type)}&filterTag=FullPropertyAddress";
                var searchHtml = await GetAsync(http, searchUrl, ct);
                if (searchHtml is null)
                {
                    return null;
                }

                account = PickAccount(searchHtml, parsed.Value);
                if (account is null)
                {
                    logger.LogWarning("Tulsa assessor: no unique account found for the address.");
                    return null;
                }
            }

            var infoHtml = await GetAsync(http, $"{_o.BaseUrl.TrimEnd('/')}/Property/Info?accountNo={Uri.EscapeDataString(account)}", ct);
            var value = infoHtml is null ? null : ParseMarketValue(infoHtml);
            return value is > 0m ? new AssetFeedQuote(value.Value, asOf, "Tulsa County Assessor market value") : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Tulsa assessor feed failed");
            return null;
        }
    }

    private async Task<string?> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "FlowLedger-valuation/1.0 (personal use)");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            logger.LogWarning("Tulsa assessor returned HTTP {Status}", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadAsStringAsync(ct);
    }

    private static string? Attr(Asset asset, string key) =>
        asset.Attributes.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static readonly HashSet<string> Directions = new(StringComparer.OrdinalIgnoreCase) { "N", "S", "E", "W", "NE", "NW", "SE", "SW" };

    /// <summary>Splits "123 S Main St, Tulsa, OK 74103" into the assessor's street-number/direction/name/type fields.</summary>
    internal static (string No, string PreDir, string Name, string Type, string? Zip)? ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var zip = ZipRegex().Match(address) is { Success: true } z ? z.Value : null;
        var tokens = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count < 2 || !tokens[0].All(char.IsDigit))
        {
            return null;
        }

        var no = tokens[0];
        tokens.RemoveAt(0);
        var pre = "";
        if (tokens.Count > 1 && Directions.Contains(tokens[0].TrimEnd('.')))
        {
            pre = tokens[0].TrimEnd('.').ToUpperInvariant();
            tokens.RemoveAt(0);
        }

        // Drop the trailing street type ("St", "Ave"); the assessor matches on name and we confirm via ZIP.
        var type = "";
        var name = tokens.Count > 1 ? string.Join(' ', tokens.Take(tokens.Count - 1)) : tokens[0];

        return (no, pre, name, type, zip);
    }

    /// <summary>
    /// Returns the single AccountNo whose street matches the number, direction and name exactly (the site's
    /// search is loose), narrowed by ZIP when several remain; null when zero or still ambiguous.
    /// </summary>
    internal static string? PickAccount(string html, (string No, string PreDir, string Name, string Type, string? Zip) want)
    {
        var prefix = NormalizeStreet(string.Join(' ', new[] { want.No, want.PreDir, want.Name }.Where(x => x.Length > 0)));
        var rows = RowRegex().Matches(html)
            .Select(m => (Account: m.Groups["acct"].Value, Zip: m.Groups["zip"].Value, Street: NormalizeStreet(m.Groups["street"].Value)))
            .Where(r => r.Street == prefix || r.Street.StartsWith(prefix + " ", StringComparison.Ordinal))
            .DistinctBy(r => r.Account)
            .ToList();
        if (rows.Count > 1 && want.Zip is not null)
        {
            rows = rows.Where(r => r.Zip.StartsWith(want.Zip, StringComparison.Ordinal)).ToList();
        }

        return rows.Count == 1 ? rows[0].Account : null;
    }

    /// <summary>Upper-cases and drops ordinal suffixes ("71st" and "71" both become "71") so names compare equal.</summary>
    internal static string NormalizeStreet(string street) =>
        OrdinalRegex().Replace(street.ToUpperInvariant(), "$1");

    /// <summary>Reads the headline "$1,234,567" market value that follows the MarketValueHeader icon.</summary>
    internal static decimal? ParseMarketValue(string html)
    {
        var m = MarketValueRegex().Match(html);
        return m.Success && decimal.TryParse(m.Groups["v"].Value.Replace(",", ""), out var v) ? v : null;
    }

    [GeneratedRegex(@"\b(\d+)(?:ST|ND|RD|TH)\b")]
    private static partial Regex OrdinalRegex();

    [GeneratedRegex(@"\b\d{5}\b")]
    private static partial Regex ZipRegex();

    [GeneratedRegex("\"TaxYear\":\\d+,\"AccountNo\":\"(?<acct>[^\"]+)\".*?\"PropertyZipCode\":\"(?<zip>[^\"]*)\".*?\"FullPropertyStreet\":\"(?<street>[^\"]*)\"", RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"id=""MarketValueHeader""[^>]*></i>\s*\$(?<v>[\d,]+(?:\.\d+)?)")]
    private static partial Regex MarketValueRegex();
}
