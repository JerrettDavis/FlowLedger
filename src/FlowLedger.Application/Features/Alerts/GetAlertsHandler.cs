using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Budgets;
using FlowLedger.Application.Features.Goals;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Alerts;

/// <summary>Computed alerts: budgets at/over their limit, goals past their target date, and (when history is available) large charges, price increases, new subscriptions and low balances.</summary>
public sealed class GetAlertsHandler(
    EffectiveBudgetService budgets, IGoalStore goals, GetMonthlySpendByCategoryHandler spend, ITenantContext tenant, TimeProvider clock,
    ITransactionRepository? transactions = null, IAccountRepository? accounts = null)
{
    private const int HistoryDays = 200;

    public async Task<IReadOnlyList<Alert>> HandleAsync(CancellationToken ct = default)
    {
        var budgetRows = await budgets.ListAsync(null, ct);
        var spent = await spend.HandleAsync(ct);

        // Rollover applied: a budget with last month's leftover is judged against the larger limit.
        var usage = budgetRows.Select(b => new BudgetUsage(b.Row.Category, b.EffectiveLimit, MonthlySpend.SpentFor(b.Row.Category, spent)));
        var progress = (await goals.ListAsync(tenant.TenantId, ct)).Select(g => new GoalProgress(g.Name, g.TargetAmount, g.SavedAmount, g.TargetDate));

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var alerts = AlertBuilder.Build(usage, progress, today).ToList();
        alerts.AddRange(await ActivityAsync(today, ct));
        return alerts.OrderBy(a => a.Severity == "error" ? 0 : 1).ToList();
    }

    private async Task<IEnumerable<Alert>> ActivityAsync(DateOnly today, CancellationToken ct)
    {
        if (transactions is null || accounts is null)
        {
            return [];
        }

        var accountList = await accounts.ListAsync(ct);
        var visible = accountList.Where(a => !a.TransactionsHidden).Select(a => a.AccountId).ToList();
        if (visible.Count == 0)
        {
            return [];
        }

        var dtos = (await transactions.ListForAccountsAsync(visible, today.AddDays(-HistoryDays), today, ct)).Select(TransactionMapper.ToDto).ToList();
        var types = accountList.ToDictionary(a => a.Id, a => a.AccountType.ToString());
        var balances = accountList.Select(a => new BalanceInput(
            a.Id, a.Name, a.CurrentBalance.Amount,
            a.AccountType is Domain.Aggregates.AccountType.Checking or Domain.Aggregates.AccountType.Savings or Domain.Aggregates.AccountType.Cash)).ToList();
        return ActivityAlertRules.Build(today, dtos, TransferDetector.Detect(dtos, types), balances, new Dictionary<Guid, string>())
            .Select(a => new Alert(a.Severity, a.Title, a.Detail));
    }
}
