using FlowLedger.Infrastructure.DataSafety;
using FlowLedger.Infrastructure.Persistence;
using FlowLedger.Infrastructure.Persistence.Entities;
using FlowLedger.Integrations.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FlowLedger.Infrastructure.HostStores;

/// <summary>
/// <see cref="IProviderCallLedger"/> through EF (#154): the host-wide <c>provider_api_calls</c> table has no tenant column, so it is
/// reached through an explicitly unscoped context (<see cref="HostDb"/>) and listed as global in the tenant guard tests. Rows are
/// append-only and are part of every snapshot/backup.
/// </summary>
public sealed class ProviderCallLedger : IProviderCallLedger
{
    private readonly HostDb _host;

    public ProviderCallLedger(string connectionString) => _host = new HostDb(connectionString);

    public async Task RecordAsync(ProviderCallRecord call, CancellationToken ct = default)
    {
        // Metric first: it must count even when the ledger write below fails.
        FlowLedger.Infrastructure.Observability.FlowLedgerTelemetry.RecordProviderCall(call.Provider, call.StatusCode, call.Error is not null);
        await using var db = _host.Create();
        db.ProviderApiCalls.Add(ProviderApiCallRecord.Create(
            call.Provider, call.Method, call.Path, call.StatusCode, call.DurationMs, call.ResponseBytes, call.OccurredUtc, call.Error));
        await db.SaveChangesAsync(ct);
    }

    public async Task<long> CountSinceAsync(string provider, DateTimeOffset sinceUtc, CancellationToken ct = default)
    {
        await using var db = _host.Create();
        return await db.ProviderApiCalls.AsNoTracking().LongCountAsync(c => c.Provider == provider && c.OccurredUtc >= sinceUtc, ct);
    }

    /// <summary>Usage summary for the UI: calls today / this month and the per-endpoint split for this month.</summary>
    public async Task<ProviderUsageSummary> GetUsageAsync(string provider, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var day = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);
        var month = new DateTimeOffset(new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc));
        var today = await CountSinceAsync(provider, day, ct);
        var monthly = await CountSinceAsync(provider, month, ct);

        await using var db = _host.Create();
        var byPath = await db.ProviderApiCalls.AsNoTracking()
            .Where(c => c.Provider == provider && c.OccurredUtc >= month)
            .GroupBy(c => new { c.Method, c.Path })
            .Select(g => new { g.Key.Method, g.Key.Path, Calls = g.LongCount() })
            .OrderByDescending(x => x.Calls)
            .Take(25)
            .ToListAsync(ct);

        return new ProviderUsageSummary(
            provider, today, monthly, byPath.Select(x => new ProviderEndpointUsage(x.Method, x.Path, x.Calls)).ToList());
    }
}

public sealed record ProviderEndpointUsage(string Method, string Path, long Calls);

public sealed record ProviderUsageSummary(string Provider, long CallsToday, long CallsThisMonth, IReadOnlyList<ProviderEndpointUsage> ByEndpoint);
