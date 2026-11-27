using FlowLedger.Application.Abstractions;
using FlowLedger.Application.Features.Alerts;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Goals;

/// <summary>
/// A goal with the progress basis resolved (#70). <paramref name="CurrentAmount"/> is what the goal has: the linked account's balance
/// when the goal is linked to a known active account, otherwise the hand-entered <paramref name="SavedAmount"/>.
/// <paramref name="AccountMonthlyTrend"/> is that account's 90-day balance change per month (null when unlinked or too little history).
/// </summary>
public sealed record GoalStatusRow(
    Guid Id, string Name, decimal TargetAmount, decimal SavedAmount, decimal CurrentAmount, DateOnly? TargetDate, uint Version,
    Guid? LinkedAccountId, string? LinkedAccountName, decimal? AccountMonthlyTrend);

/// <summary>Lists the tenant's goals with their progress resolved against linked accounts (#70).</summary>
internal sealed class GetGoalStatusesHandler(
    IGoalStore store, IAccountRepository accounts, ITransactionRepository transactions, ITenantContext tenant, TimeProvider clock)
{
    public async Task<IReadOnlyList<GoalStatusRow>> HandleAsync(CancellationToken ct = default)
    {
        var rows = await store.ListAsync(tenant.TenantId, ct);
        if (rows.All(g => g.LinkedAccountId is null))
        {
            return rows.Select(g => new GoalStatusRow(g.Id, g.Name, g.TargetAmount, g.SavedAmount, g.SavedAmount, g.TargetDate, g.Version, null, null, null)).ToList();
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var accountList = await accounts.ListAsync(ct);
        var balances = accountList.Select(a => new BalanceInput(a.Id, a.Name, a.CurrentBalance.Amount, true)).ToList();
        var linkedVisible = accountList
            .Where(a => !a.TransactionsHidden && rows.Any(g => g.LinkedAccountId == a.Id))
            .Select(a => a.AccountId).ToList();
        IReadOnlyList<TransactionDto> history = linkedVisible.Count == 0
            ? []
            : (await transactions.ListForAccountsAsync(linkedVisible, today.AddDays(-LinkedAccountTrend.HistoryDays), today, ct)).Select(TransactionMapper.ToDto).ToList();

        var paces = GoalPaces.Resolve(today, rows, balances, history).ToDictionary(p => p.Id);
        return rows.Select(g =>
        {
            var pace = paces[g.Id];
            return new GoalStatusRow(g.Id, g.Name, g.TargetAmount, g.SavedAmount, pace.Saved, g.TargetDate, g.Version,
                pace.Linked?.AccountId, pace.Linked?.AccountName, pace.Linked?.MonthlyTrend);
        }).ToList();
    }
}
