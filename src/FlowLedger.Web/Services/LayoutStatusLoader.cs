using FlowLedger.Web.ApiClient;

namespace FlowLedger.Web.Services;

/// <summary>What the app bar needs to know before it can show the right actions.</summary>
public sealed record LayoutStatus(string Environment, string? BackupAlert)
{
    public const string UnknownEnvironment = "unknown";

    public static LayoutStatus Unknown { get; } = new(UnknownEnvironment, null);

    /// <summary>False until the API has told us the environment; the layout shows a placeholder until then.</summary>
    public bool IsKnown => Environment != UnknownEnvironment;
}

/// <summary>Loads the layout's startup status with both API calls in flight at once.</summary>
public static class LayoutStatusLoader
{
    public static async Task<LayoutStatus> LoadAsync(FlowLedgerApiClient api, CancellationToken ct = default)
    {
        try
        {
            var envTask = api.GetEnvironmentAsync(ct);
            var dataTask = api.GetDataStatusAsync(ct);
            await Task.WhenAll(envTask, dataTask);

            var environment = envTask.Result?.Environment ?? LayoutStatus.UnknownEnvironment;
            var data = dataTask.Result;
            var alert = data is null ? null
                : data.Backup.Stale ? "No recent successful backup — open Data & Backups"
                : !data.Backup.OffsiteConfigured && environment == "production" ? "No off-site backup configured"
                : null;
            return new LayoutStatus(environment, alert);
        }
        catch (Exception ex) when (ex is ApiClientException or HttpRequestException or OperationCanceledException)
        {
            return LayoutStatus.Unknown;
        }
    }
}
