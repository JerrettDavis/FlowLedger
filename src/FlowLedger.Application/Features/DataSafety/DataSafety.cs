using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.DataSafety;

/// <summary>One backup run as the Data page shows it (host-wide, no tenant data).</summary>
public sealed record BackupRunView(
    bool Ok, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string? SnapshotDir, long Rows,
    bool? OffsiteOk, string? OffsiteDetail, bool? DrillOk, string? Error);

/// <summary>One finished background job run.</summary>
public sealed record JobRunView(string Job, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, bool Success, string? Error, int Attempt);

public sealed record ProviderEndpointUsageView(string Method, string Path, long Calls);

/// <summary>Billable provider usage with the budgets it is measured against.</summary>
public sealed record ProviderUsageView(
    string Provider, long CallsToday, long CallsThisMonth, int DailyBudget, int MonthlyBudget, bool Paused,
    IReadOnlyList<ProviderEndpointUsageView> ByEndpoint);

/// <summary>Port: reads the host-wide data-safety facts (backup history, job history, provider usage). Implemented in Infrastructure.</summary>
public interface IDataSafetyReader
{
    /// <summary>Data environment of this process ("demo", "sandbox", "production"), or "unknown" before the guard has run.</summary>
    string Environment { get; }

    bool OffsiteConfigured { get; }

    Task<IReadOnlyList<BackupRunView>> RecentBackupsAsync(int limit, CancellationToken ct = default);

    Task<IReadOnlyList<JobRunView>> RecentJobRunsAsync(int limit, CancellationToken ct = default);

    Task<ProviderUsageView> ProviderUsageAsync(DateTimeOffset nowUtc, CancellationToken ct = default);
}

/// <summary>What an import produced. <see cref="Report"/> is the detailed report the client receives unchanged on success.</summary>
public sealed record DataImportOutcome(bool Ok, IReadOnlyList<string> Problems, object Report);

/// <summary>Port: whole-database export and import (never request-supplied paths: the working folders are host configuration).</summary>
public interface IDataArchive
{
    /// <summary>Writes an export zip and returns its path; the caller deletes it when sent. <paramref name="onlyTenant"/> null exports every tenant.</summary>
    Task<string> ExportAsync(Guid? onlyTenant, CancellationToken ct = default);

    Task<DataImportOutcome> ImportAsync(Stream zip, CancellationToken ct = default);
}

/// <summary>Backup health: last run succeeded, finished within <see cref="StaleAfter"/>, and (if configured) reached off-site.</summary>
public sealed record DataStatus(
    string Environment, bool BackupHealthy, bool BackupStale, bool OffsiteConfigured, DateTimeOffset? LastSuccessUtc,
    BackupRunView? LastRun, IReadOnlyList<BackupRunView> Runs, IReadOnlyList<JobRunView> JobRuns, ProviderUsageView ProviderUsage);

/// <summary>Composes the Data page status from the reader port; the staleness rule lives here so it is testable without HTTP (#94).</summary>
public sealed class GetDataStatusHandler(IDataSafetyReader reader, TimeProvider clock)
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(36);

    private const int RecentBackups = 10;
    private const int RecentJobs = 20;

    public async Task<DataStatus> HandleAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var runs = await reader.RecentBackupsAsync(RecentBackups, ct);
        var lastOk = runs.FirstOrDefault(r => r.Ok);
        var last = runs is { Count: > 0 } ? runs[0] : null;
        var stale = last is null || !last.Ok || now - last.FinishedUtc > StaleAfter;
        var jobRuns = await reader.RecentJobRunsAsync(RecentJobs, ct);
        var usage = await reader.ProviderUsageAsync(now, ct);
        return new DataStatus(reader.Environment, !stale, stale, reader.OffsiteConfigured, lastOk?.FinishedUtc, last, runs, jobRuns, usage);
    }
}

/// <summary>Result of <see cref="ExportDataHandler"/>: a zip to send, or why the request was refused.</summary>
public sealed record ExportDataOutcome(string? ZipPath, bool AllTenants, string? Refusal)
{
    public bool Refused => Refusal is not null;
}

/// <summary>
/// Export scope rules (#139): the default stays the whole database (disaster recovery); <c>scope=tenant</c> exports only the caller's tenant;
/// an explicit tenant id exports that tenant.
/// </summary>
public sealed class ExportDataHandler(IDataArchive archive, ITenantContext tenant)
{
    public async Task<ExportDataOutcome> HandleAsync(string? scope, Guid? tenantId, CancellationToken ct = default)
    {
        var all = scope is null || string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);
        var own = string.Equals(scope, "tenant", StringComparison.OrdinalIgnoreCase);
        if (!all && !own)
        {
            return new ExportDataOutcome(null, false, "scope must be 'all' or 'tenant'");
        }

        Guid? only = tenantId ?? (own ? tenant.TenantId : null);
        var zip = await archive.ExportAsync(only, ct);
        return new ExportDataOutcome(zip, only is null, null);
    }
}

/// <summary>Imports an export zip into an empty database; the archive port enforces the size, path and emptiness guards.</summary>
public sealed class ImportDataHandler(IDataArchive archive)
{
    public Task<DataImportOutcome> HandleAsync(Stream zip, CancellationToken ct = default) => archive.ImportAsync(zip, ct);
}
