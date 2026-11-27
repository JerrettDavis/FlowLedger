using System.Globalization;
using FlowLedger.Application.Features.Strategies;
using Microsoft.Extensions.Logging;

namespace FlowLedger.Infrastructure.Strategies;

/// <summary>
/// Fetches public benchmark rates from the St. Louis Fed's FRED CSV endpoint (no API key, no user data sent): Freddie Mac mortgage
/// averages, Treasury yields (used to estimate deposit/CD yields), and Federal Reserve consumer loan/credit card rates.
/// Results are cached; any failure falls back per-field to labelled assumptions, never to an exception.
/// </summary>
internal sealed class FredMarketRateProvider(HttpClient http, ILogger<FredMarketRateProvider> logger) : IMarketRateProvider
{
    public const string BaseUrl = "https://fred.stlouisfed.org/graph/fredgraph.csv?id=";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MarketRates? _cached;
    private static DateTimeOffset _expires;

    /// <summary>Spread (percentage points) retail products typically pay below the matching Treasury yield.</summary>
    private const decimal CdSpread = 0.10m;
    private const decimal SavingsSpread = 0.25m;
    private const decimal MoneyMarketSpread = 0.35m;

    public async Task<MarketRates> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow < _expires)
        {
            return _cached;
        }

        await Gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow < _expires)
            {
                return _cached;
            }

            var rates = await FetchAsync(ct);
            _cached = rates;
            _expires = DateTimeOffset.UtcNow + (rates.Live.Count > 0 ? Ttl : Retry);
            return rates;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<MarketRates> FetchAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var d = MarketRates.Defaults(today);
        var series = new[] { "MORTGAGE30US", "MORTGAGE15US", "DGS3MO", "DGS6MO", "DGS1", "DGS2", "DGS5", "TERMCBAUTO48NS", "TERMCBPER24NS", "TERMCBCCALLNS" };
        var results = await Task.WhenAll(series.Select(s => LatestAsync(s, ct)));
        var v = series.Zip(results).Where(x => x.Second is not null).ToDictionary(x => x.First, x => x.Second!.Value);
        var live = v.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (live.Count == 0)
        {
            return d;
        }

        decimal Get(string id, decimal fallback) => v.TryGetValue(id, out var x) ? x.Value : fallback;
        decimal Dep(string id, decimal fallback, decimal spread) => v.TryGetValue(id, out var x) ? Math.Max(0.10m, x.Value - spread) : fallback;

        var asOf = v.Values.Max(x => x.Date);
        return d with
        {
            AsOf = asOf,
            Hysa = Dep("DGS3MO", d.Hysa, SavingsSpread),
            MoneyMarket = Dep("DGS3MO", d.MoneyMarket, MoneyMarketSpread),
            Cd6Month = Dep("DGS6MO", d.Cd6Month, CdSpread),
            Cd1Year = Dep("DGS1", d.Cd1Year, CdSpread),
            Cd2Year = Dep("DGS2", d.Cd2Year, CdSpread),
            Cd5Year = Dep("DGS5", d.Cd5Year, CdSpread),
            Mortgage30 = Get("MORTGAGE30US", d.Mortgage30),
            Mortgage15 = Get("MORTGAGE15US", d.Mortgage15),
            Auto48Month = Get("TERMCBAUTO48NS", d.Auto48Month),
            PersonalLoan = Get("TERMCBPER24NS", d.PersonalLoan),
            CreditCard = Get("TERMCBCCALLNS", d.CreditCard),
            Live = live,
            Source = $"FRED (St. Louis Fed): {live.Count}/{series.Length} series live; deposit and CD yields estimated from Treasury yields; the rest are assumptions",
        };
    }

    private async Task<(DateOnly Date, decimal Value)?> LatestAsync(string id, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var csv = await http.GetStringAsync(BaseUrl + id, cts.Token);
            return ParseLatest(csv);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("Market rate series {Series} unavailable: {Reason}", id, ex.Message);
            return null;
        }
    }

    /// <summary>Last row with a numeric value from a FRED CSV (date,value; "." marks missing).</summary>
    public static (DateOnly Date, decimal Value)? ParseLatest(string csv)
    {
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var parts = line.Trim().Split(',');
            if (parts.Length >= 2
                && DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                && decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                && value is > 0 and < 100)
            {
                return (date, value);
            }
        }

        return null;
    }
}
