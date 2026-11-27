using FlowLedger.Application.Features.DataSafety;
using FlowLedger.Infrastructure.HostStores;
using Microsoft.Extensions.Configuration;

namespace FlowLedger.Infrastructure.DataSafety;

/// <summary>Reads backup history, job history and billable provider usage for the Data page (host-wide; no tenant data).</summary>
internal sealed class DataSafetyReader(
    BackupRunStore backups, ProviderCallLedger ledger, IJobRunStore jobRuns, DataEnvironmentState environment, BackupSettings settings, IConfiguration configuration)
    : IDataSafetyReader
{
    private const string Provider = FlowLedger.Integrations.Abstractions.ConnectorIds.Mx;

    private const int DefaultDailyBudget = 500;
    private const int DefaultMonthlyBudget = 5000;

    public string Environment => environment.Current?.ToString().ToLowerInvariant() ?? "unknown";

    public bool OffsiteConfigured => settings.OffsiteConfigured;

    public async Task<IReadOnlyList<BackupRunView>> RecentBackupsAsync(int limit, CancellationToken ct = default) =>
        (await backups.RecentAsync(limit, ct))
        .Select(r => new BackupRunView(r.Ok, r.StartedUtc, r.FinishedUtc, r.SnapshotDir, r.Rows, r.OffsiteOk, r.OffsiteDetail, r.DrillOk, r.Error))
        .ToList();

    public async Task<IReadOnlyList<JobRunView>> RecentJobRunsAsync(int limit, CancellationToken ct = default) =>
        (await jobRuns.RecentAsync(limit, ct))
        .Select(r => new JobRunView(r.Job, r.StartedUtc, r.FinishedUtc, r.Success, r.Error, r.Attempt))
        .ToList();

    public async Task<ProviderUsageView> ProviderUsageAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var usage = await ledger.GetUsageAsync(Provider, nowUtc, ct);
        return new ProviderUsageView(
            Provider, usage.CallsToday, usage.CallsThisMonth,
            configuration.GetValue("Mx:Provider:DailyCallBudget", DefaultDailyBudget),
            configuration.GetValue("Mx:Provider:MonthlyCallBudget", DefaultMonthlyBudget),
            configuration.GetValue("Mx:Provider:CallsPaused", false),
            usage.ByEndpoint.Select(e => new ProviderEndpointUsageView(e.Method, e.Path, e.Calls)).ToList());
    }
}

/// <summary>Whole-database export and import over <see cref="SnapshotImport"/>; the working folders come from <see cref="BackupSettings"/>, never from a request.</summary>
internal sealed class DataArchive(IConfiguration configuration, BackupSettings settings) : IDataArchive
{
    public Task<string> ExportAsync(Guid? onlyTenant, CancellationToken ct = default) =>
        SnapshotImport.ExportZipAsync(Connection(), Path.Combine(settings.BackupRoot, "exports"), ct, onlyTenant);

    public async Task<DataImportOutcome> ImportAsync(Stream zip, CancellationToken ct = default)
    {
        var result = await SnapshotImport.ImportZipAsync(Connection(), zip, Path.Combine(settings.BackupRoot, "imports"), ct);
        return new DataImportOutcome(result.Ok, result.Problems, result);
    }

    private string Connection() => configuration.GetConnectionString("flowledger")!;
}
