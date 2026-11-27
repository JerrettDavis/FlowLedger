using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Shared;
using Microsoft.JSInterop;
using MudBlazor;

namespace FlowLedger.Web.Services;

/// <summary>
/// The app bar's sync and bank-connect behaviour, kept out of the layout so it can be tested on its own.
/// While the app is on screen it syncs on load if the last sync is more than an hour old, then re-checks every few minutes.
/// Hidden tabs never sync (the Worker covers that daily). The server still enforces single-flight, its minimum interval and the
/// provider call budget; any failure or throttle backs the browser off so it never hammers a limited API.
/// </summary>
public sealed partial class SyncCoordinator(
    FlowLedgerApiClient api,
    ISnackbar snackbar,
    IJSRuntime js,
    IDialogService dialogs,
    ILogger<SyncCoordinator> logger) : IDisposable
{
    private static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan AutoSyncTick = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AutoSyncBackoff = TimeSpan.FromHours(2);

    private readonly CancellationTokenSource _cts = new();
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private bool _started;

    /// <summary>True while a sync or connect is running (the buttons disable themselves).</summary>
    public bool Syncing { get; private set; }

    /// <summary>Text for the polite live region: what just happened ("Syncing", "Sync complete: 3 new transactions").</summary>
    public string Announcement { get; private set; } = "";

    /// <summary>Raised whenever <see cref="Syncing"/> or <see cref="Announcement"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Begins the auto-sync loop once per circuit; later calls do nothing.</summary>
    public void StartAutoSync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = RunLoopAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    public Task SyncNowAsync() => RunSyncAsync(auto: false);

    public async Task ConnectAndSyncAsync()
    {
        Set(syncing: true, "Syncing");
        try
        {
            await api.ConnectAsync();
            var result = await api.SyncAsync();
            var msg = result is not null
                ? $"Sync complete — {result.AccountsUpserted} accounts, {result.TransactionsAdded} transactions added, {result.TransactionsSkipped} skipped, {result.RecurringFlowsAdded} recurring flows added"
                : "Sync complete";
            Announcement = msg;
            snackbar.Add(msg, Severity.Success);
        }
        catch (Exception ex)
        {
            LogConnectAndSyncFailed(ex);
            Announcement = "Sync failed";
            snackbar.AddFailure(ex, "Sync failed");
        }
        finally
        {
            Set(syncing: false, Announcement);
        }
    }

    public async Task ConnectBankAsync()
    {
        // In-app flow first: when Quiltt is enabled the user links a bank in the Connector without leaving FlowLedger.
        var quiltt = await api.GetQuilttConfigAsync();
        if (quiltt.Enabled)
        {
            await ConnectViaQuilttAsync();
            return;
        }

        var info = await api.GetProviderInfoAsync();
        if (info is { UsesWidget: false, ManageUrl: { Length: > 0 } manage })
        {
            // SimpleFIN Bridge: institutions are linked on the Bridge site; the access URL is already configured here.
            await js.InvokeVoidAsync("open", manage, "_blank");
            snackbar.Add($"{info.Provider}: link or re-authenticate institutions on the Bridge page, then press Sync now.", Severity.Info);
            return;
        }

        var ok = await dialogs.ShowMessageBoxAsync(
            "Connect a bank",
            "This opens your provider's secure connection widget. Creating the connection is a billable provider call, and it is only made once. Continue?",
            yesText: "Continue", cancelText: "Cancel");
        if (ok != true)
        {
            return;
        }

        try
        {
            var url = await api.BeginBankConnectionAsync();
            if (string.IsNullOrWhiteSpace(url))
            {
                snackbar.Add("The provider did not return a connection link.", Severity.Warning);
                return;
            }

            await js.InvokeVoidAsync("open", url, "_blank");
            snackbar.Add("Finish linking in the new tab, then press Sync now.", Severity.Info);
        }
        catch (Exception ex)
        {
            LogConnectBankFailed(ex);
            snackbar.AddFailure(ex, "Connect failed");
        }
    }

    private async Task ConnectViaQuilttAsync()
    {
        try
        {
            var session = await api.StartQuilttSessionAsync();
            if (session is null)
            {
                snackbar.Add("Could not start the bank connection.", Severity.Warning);
                return;
            }

            var result = await js.InvokeAsync<QuilttConnectResult>(
                "flowledgerQuiltt.open", session.ScriptUrl, session.ConnectorId, session.Token);
            if (result.Status == "success")
            {
                snackbar.Add("Bank linked. Syncing your accounts…", Severity.Success);
                await SyncNowAsync();
            }
            else if (result.Status == "error")
            {
                snackbar.Add("The bank connection did not complete. Please try again.", Severity.Warning);
            }
        }
        catch (Exception ex)
        {
            LogQuilttConnectFailed(ex);
            snackbar.AddFailure(ex, "Connect failed");
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await AutoSyncIfDueAsync();
            using var timer = new PeriodicTimer(AutoSyncTick);
            while (await timer.WaitForNextTickAsync(ct))
            {
                await AutoSyncIfDueAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            LogAutoSyncLoopStopped(ex);
        }
    }

    private async Task AutoSyncIfDueAsync()
    {
        if (Syncing || DateTimeOffset.UtcNow < _backoffUntil)
        {
            return;
        }

        try
        {
            if (!await js.InvokeAsync<bool>("FlowLedgerPage.isVisible"))
            {
                return; // not on display: leave it to the daily Worker sync
            }

            // Source of truth is the server (survives reloads and covers syncs from other processes).
            var status = await api.GetSyncStatusAsync();
            if (status is null)
            {
                return;
            }

            var wait = (status.LastSyncedAt is { } last ? last + AutoSyncInterval : DateTimeOffset.MinValue) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero || status.Provider == "Simulated")
            {
                return;
            }

            await RunSyncAsync(auto: true);
        }
        catch (Exception ex)
        {
            _backoffUntil = DateTimeOffset.UtcNow + AutoSyncBackoff;
            LogAutoSyncCheckFailed(ex, AutoSyncBackoff);
        }
    }

    private async Task RunSyncAsync(bool auto)
    {
        Set(syncing: true, "Syncing");
        try
        {
            var result = await api.SyncAsync();
            if (auto)
            {
                if (result is { TransactionsAdded: > 0 })
                {
                    snackbar.Add($"Auto-sync: {result.TransactionsAdded} new transactions", Severity.Info);
                    Announcement = $"Sync finished: {result.TransactionsAdded} new transactions";
                }
                else
                {
                    Announcement = "Sync finished";
                }

                return;
            }

            Announcement = result is not null ? $"Sync complete: {result.TransactionsAdded} new transactions" : "Sync complete";
            snackbar.Add(result is not null ? $"Sync complete — {result.TransactionsAdded} new transactions" : "Sync complete", Severity.Success);
        }
        catch (Exception ex)
        {
            LogSyncFailed(ex);
            if (auto)
            {
                // Throttled / budget / needs-reauth / provider outage: stay quiet and back off.
                _backoffUntil = DateTimeOffset.UtcNow + AutoSyncBackoff;
                Announcement = "";
            }
            else
            {
                Announcement = "Sync failed";
                snackbar.AddFailure(ex, "Sync failed");
            }
        }
        finally
        {
            Set(syncing: false, Announcement);
        }
    }

    private void Set(bool syncing, string announcement)
    {
        Syncing = syncing;
        Announcement = announcement;
        Changed?.Invoke();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Connect & Sync failed")]
    private partial void LogConnectAndSyncFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connect bank failed")]
    private partial void LogConnectBankFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Quiltt connect failed")]
    private partial void LogQuilttConnectFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-sync loop stopped")]
    private partial void LogAutoSyncLoopStopped(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-sync check failed; backing off for {Backoff}")]
    private partial void LogAutoSyncCheckFailed(Exception exception, TimeSpan backoff);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync failed")]
    private partial void LogSyncFailed(Exception exception);
}
