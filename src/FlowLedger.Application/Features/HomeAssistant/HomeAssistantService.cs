using System.Globalization;
using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Forecasting;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.HomeAssistant;

public sealed record HaRunResult(int Detected, int Sent, int AlreadySent, int Failed, string? Error);

/// <summary>
/// Detects alert-worthy events for the current tenant and pushes the new ones to Home Assistant. Safe to call repeatedly:
/// each event is claimed by its dedupe key first, so it is delivered once; a failed delivery releases the claim and retries
/// next run. Sends nothing unless the household enabled sharing AND stored a webhook.
/// </summary>
public sealed class HomeAssistantService(
    IHomeAssistantStore store,
    IHomeAssistantSender sender,
    IHomeAssistantNetworkPolicy network,
    IAccountRepository accounts,
    IRecurringFlowRepository flows,
    ITransactionRepository transactions,
    EffectiveBudgetService budgets,
    GetMonthlySpendByCategoryHandler spend,
    GetForecastHandler forecast,
    ITenantContext tenant,
    TimeProvider clock) : IAlertEventSource
{
    private readonly TimeProvider _clock = clock;

    public const int HorizonDays = 14;
    public const int ForecastRiskDays = 7;
    public const int HistoryDays = 12;
    public const decimal BudgetWarnPercent = 80m;

    /// <summary>The single clock for events (server-local date), so a manual send and the scheduled run share dedupe keys.</summary>
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    /// <summary>What would be sent now under the saved settings, with amounts and balances always omitted.</summary>
    public async Task<IReadOnlyList<HaEvent>> PreviewAsync(CancellationToken ct = default)
        => await DetectAsync(Today, (await store.GetAsync(tenant.TenantId, ct)) with { ShareAmounts = false }, ct);

    public async Task<HaRunResult> RunAsync(CancellationToken ct = default)
    {
        var today = Today;
        var settings = await store.GetAsync(tenant.TenantId, ct);
        if (!settings.Enabled || !settings.HasWebhook)
        {
            return new HaRunResult(0, 0, 0, 0, null);
        }

        var url = await store.GetWebhookUrlAsync(tenant.TenantId, ct);
        if (url is null)
        {
            return new HaRunResult(0, 0, 0, 0, null);
        }

        var events = await DetectAsync(today, settings, ct);
        int sent = 0, already = 0, failed = 0;
        string? lastError = null;
        foreach (var e in events)
        {
            if (!await store.TryClaimAsync(tenant.TenantId, e.DedupeKey, ct))
            {
                already++;
                continue;
            }

            var error = await sender.SendAsync(url, ToPayload(e), network.AllowLocalNetwork(tenant.TenantId), ct);
            if (error is null)
            {
                sent++;
                continue;
            }

            failed++;
            lastError = error;
            await store.ReleaseAsync(tenant.TenantId, e.DedupeKey, ct);
            break; // the endpoint is unreachable or rejecting us; do not hammer it per event
        }

        if (sent > 0 || failed > 0)
        {
            await store.RecordResultAsync(tenant.TenantId, lastError, ct);
        }

        await store.PruneAsync(tenant.TenantId, _clock.GetUtcNow().AddDays(-90), ct);
        return new HaRunResult(events.Count, sent, already, failed, lastError);
    }

    /// <summary>Sends a harmless test event so the household can wire up their automation. Returns null on success.</summary>
    public async Task<string?> SendTestAsync(CancellationToken ct = default)
    {
        var url = await store.GetWebhookUrlAsync(tenant.TenantId, ct);
        if (url is null)
        {
            return "Save a webhook URL first.";
        }

        var error = await sender.SendAsync(url, new Dictionary<string, object?>
        {
            ["source"] = "flowledger",
            ["version"] = 1,
            ["kind"] = "test",
            ["severity"] = "info",
            ["title"] = "FlowLedger test",
            ["message"] = "Home Assistant can hear FlowLedger.",
            ["event_id"] = Guid.NewGuid().ToString("N"),
        }, network.AllowLocalNetwork(tenant.TenantId), ct);
        await store.RecordResultAsync(tenant.TenantId, error, ct);
        return error;
    }

    /// <summary>The wire payload. Numeric fields appear only when the event carries them, which needs ShareAmounts on.</summary>
    public static IReadOnlyDictionary<string, object?> ToPayload(HaEvent e)
    {
        var p = new Dictionary<string, object?>
        {
            ["source"] = "flowledger",
            ["version"] = 1,
            ["kind"] = e.Kind,
            ["severity"] = e.Severity,
            ["title"] = e.Title,
            ["message"] = e.Message,
            ["event_id"] = e.DedupeKey,
        };
        if (e.Subject is not null)
        {
            p["subject"] = e.Subject;
        }

        if (e.Date is { } d)
        {
            p["date"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (e.DueInDays is { } due)
        {
            p["due_in_days"] = due;
        }

        if (e.Amount is { } amount)
        {
            p["amount"] = amount;
        }

        if (e.Balance is { } balance)
        {
            p["balance"] = balance;
        }

        return p;
    }

    public async Task<IReadOnlyList<HaEvent>> DetectAsync(DateOnly today, HomeAssistantSettings settings, CancellationToken ct)
    {
        var accountList = await accounts.ListAsync(ct); // active accounts only
        var flowList = (await flows.ListAsync(ct)).Where(f => f.IsActive).ToList();
        var horizon = new DateOnlyRange(today.AddDays(-HistoryDays), today.AddDays(HorizonDays));

        var haFlows = flowList.Select(f => new HaFlow(
            f.Id, f.Name, f.Counterparty, f.Direction == TransactionDirection.Credit, f.Amount.Amount,
            RecurrenceExpander.Expand(f.Pattern, f.ActiveWindow.Start, f.ActiveWindow.End, horizon))).ToList();

        var visible = accountList.Where(a => !a.TransactionsHidden).Select(a => a.AccountId).ToList();
        var recentTx = visible.Count == 0
            ? []
            : await transactions.ListForAccountsAsync(visible, today.AddDays(-HistoryDays), today, ct);
        var recent = recentTx
            .Where(t => t.Status != TransactionStatus.Planned)
            .Select(t => new HaRecentTransaction(t.Description, t.MerchantName, t.Direction == TransactionDirection.Credit, t.Amount.Amount, t.EffectiveDate))
            .ToList();

        var balances = accountList.Select(a => new HaAccountBalance(
            a.Id, a.Name, a.CurrentBalance.Amount,
            a.AccountType is AccountType.Checking or AccountType.Savings or AccountType.Cash)).ToList();

        decimal? lowest = null;
        try
        {
            var result = await forecast.HandleAsync(new GetForecastQuery { AsOf = today, HorizonEnd = today.AddDays(ForecastRiskDays) }, ct);
            var soon = result.OverdraftWarnings.Where(w => w.FirstBreachDate <= today.AddDays(ForecastRiskDays)).ToList();
            if (soon.Count > 0)
            {
                lowest = soon.Min(w => w.ProjectedBalance.Amount);
            }
        }
        catch (ForecastInputException)
        {
            // No forecastable accounts yet: nothing to warn about.
        }

        var breaches = new List<HaBudgetBreach>();
        var spent = await spend.HandleAsync(ct);
        foreach (var b in await budgets.ListAsync(null, ct))
        {
            if (b.EffectiveLimit <= 0)
            {
                continue;
            }

            var used = MonthlySpend.SpentFor(b.Row.Category, spent);
            if (used > b.EffectiveLimit)
            {
                breaches.Add(new HaBudgetBreach(b.Row.Category, true));
            }
            else if (used / b.EffectiveLimit * 100m >= BudgetWarnPercent)
            {
                breaches.Add(new HaBudgetBreach(b.Row.Category, false));
            }
        }

        var activity = await DetectActivityAsync(today, accountList, visible, ct);
        return HomeAssistantEventDetector.Detect(today, haFlows, balances, recent, breaches, lowest, settings, activity);
    }

    /// <summary>Large-charge, price-increase, new-subscription and low-balance alerts from recent history.</summary>
    private async Task<IReadOnlyList<Alerts.ActivityAlert>> DetectActivityAsync(
        DateOnly today, IReadOnlyList<Domain.Aggregates.Account> accountList, List<Domain.ValueObjects.AccountId> visible, CancellationToken ct)
    {
        if (visible.Count == 0)
        {
            return [];
        }

        var history = await transactions.ListForAccountsAsync(visible, today.AddDays(-ActivityHistoryDays), today, ct);
        var dtos = history.Select(Transactions.TransactionMapper.ToDto).ToList();
        var types = accountList.ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var transferIds = Transactions.TransferDetector.Detect(dtos, types);
        var balances = accountList.Select(a => new Alerts.BalanceInput(
            a.Id, a.Name, a.CurrentBalance.Amount,
            a.AccountType is AccountType.Checking or AccountType.Savings or AccountType.Cash)).ToList();
        return Alerts.ActivityAlertRules.Build(today, dtos, transferIds, balances, new Dictionary<Guid, string>());
    }

    private const int ActivityHistoryDays = 200;
}
