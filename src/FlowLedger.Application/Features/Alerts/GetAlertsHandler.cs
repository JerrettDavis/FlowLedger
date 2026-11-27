using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Goals;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>
/// Computed alerts: budgets at/over their limit, goals past their target date, and (when history is available) large charges,
/// price increases, new subscriptions, low balances (including the account behind a goal) and goals slipping behind schedule.
/// A goal linked to an account is judged on that account's balance and 90-day trend (see <see cref="GoalSlipRules"/>).
/// </summary>
public sealed class GetAlertsHandler(
    EffectiveBudgetService budgets, IGoalStore goals, GetMonthlySpendByCategoryHandler spend, ITenantContext tenant, TimeProvider clock,
    ITransactionRepository? transactions = null, IAccountRepository? accounts = null)
{
    public async Task<IReadOnlyList<Alert>> HandleAsync(CancellationToken ct = default)
    {
        var budgetRows = await budgets.ListAsync(null, ct);
        var spent = await spend.HandleAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var accountList = accounts is null ? [] : await accounts.ListAsync(ct);
        var balances = accountList.Select(a => new BalanceInput(
            a.Id, a.Name, a.CurrentBalance.Amount,
            a.AccountType is Domain.Aggregates.AccountType.Checking or Domain.Aggregates.AccountType.Savings or Domain.Aggregates.AccountType.Cash)).ToList();
        var history = await HistoryAsync(accountList, today, ct);
        var paces = GoalPaces.Resolve(today, await goals.ListAsync(tenant.TenantId, ct), balances, history ?? []);

        // Rollover applied: a budget with last month's leftover is judged against the larger limit.
        var usage = budgetRows.Select(b => new BudgetUsage(b.Row.Category, b.EffectiveLimit, MonthlySpend.SpentFor(b.Row.Category, spent)));
        var progress = paces.Select(g => new GoalProgress(g.Name, g.Target, g.Saved, g.TargetDate));

        var alerts = AlertBuilder.Build(usage, progress, today).ToList();
        if (history is not null)
        {
            alerts.AddRange(Activity(today, accountList, balances, history, paces));
        }

        return alerts.OrderBy(a => a.Severity == "error" ? 0 : 1).ToList();
    }

    /// <summary>The recent transactions of the accounts whose transactions are visible; null when there is no history source or no such account.</summary>
    private async Task<IReadOnlyList<TransactionDto>?> HistoryAsync(IReadOnlyList<Domain.Aggregates.Account> accountList, DateOnly today, CancellationToken ct)
    {
        if (transactions is null || accounts is null)
        {
            return null;
        }

        var visible = accountList.Where(a => !a.TransactionsHidden).Select(a => a.AccountId).ToList();
        if (visible.Count == 0)
        {
            return null;
        }

        return (await transactions.ListForAccountsAsync(visible, today.AddDays(-LinkedAccountTrend.HistoryDays), today, ct)).Select(TransactionMapper.ToDto).ToList();
    }

    private static IEnumerable<Alert> Activity(
        DateOnly today, IReadOnlyList<Domain.Aggregates.Account> accountList, IReadOnlyList<BalanceInput> balances,
        IReadOnlyList<TransactionDto> history, IReadOnlyList<GoalPace> paces)
    {
        var types = accountList.ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var transferIds = TransferDetector.Detect(history, types);
        var found = ActivityAlertRules.Build(today, history, transferIds, balances, new Dictionary<Guid, string>(),
            goalsByAccount: GoalPaces.UnfinishedByAccount(paces)).ToList();
        found.AddRange(GoalSlipRules.Build(today, paces, GoalSlipRules.MonthlySurplus(today, history, transferIds)));
        return found.Select(a => new Alert(a.Severity, a.Title, a.Detail, a.Kind));
    }
}
