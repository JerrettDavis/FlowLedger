using FlowLedger.Application.Abstractions;

namespace FlowLedger.Application.Features.Accounts;

/// <summary>
/// Which accounts' transactions stay out of everyday views (transactions, dashboard, spending, reports, recurring detection).
/// Disabled accounts are always out. Hidden accounts (holdings or "hide transactions") are out unless the caller names them.
/// Consumers filter AFTER transfer detection so a transfer into a hidden account still pairs and is not counted as spending.
/// </summary>
internal static class AccountVisibility
{
    public static async Task<HashSet<Guid>> ExcludedIdsAsync(
        IAccountRepository accounts, IReadOnlySet<Guid>? explicitlySelected = null, CancellationToken ct = default)
    {
        var all = await accounts.ListIncludingInactiveAsync(ct);
        return all
            .Where(a => !a.IsActive || (a.TransactionsHidden && explicitlySelected?.Contains(a.Id) != true))
            .Select(a => a.Id)
            .ToHashSet();
    }
}
