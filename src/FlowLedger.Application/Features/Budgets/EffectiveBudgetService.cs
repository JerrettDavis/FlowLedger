using System.Globalization;
using FlowLedger.Application.Features.Alerts;
using FlowLedger.SharedKernel;

namespace FlowLedger.Application.Features.Budgets;

/// <summary>A budget as it applies to one month: the limit the user set, what rolled over into it, and the limit it is judged against.</summary>
/// <param name="Row">The stored budget (its <see cref="BudgetRow.MonthlyLimit"/> is the base limit the user set).</param>
/// <param name="Carried">Last month's unspent amount carried in (zero unless the budget has rollover on).</param>
/// <param name="EffectiveLimit">Base limit plus <paramref name="Carried"/>: what spending is compared with.</param>
/// <param name="Version">The row version to send back when editing (a carry written just now bumps it).</param>
public sealed record EffectiveBudget(BudgetRow Row, decimal Carried, decimal EffectiveLimit, uint Version);

/// <summary>
/// Applies budget rollover (#70). For a budget with rollover on, the unspent part of last month's limit
/// (<see cref="BudgetRollover.EffectiveLimit"/>) is added to the month's limit. The carried amount is worked out once per month, stored on
/// the budget, and reused, so alerts, Home Assistant and the budgets page all judge spending against the same figure.
/// </summary>
internal sealed class EffectiveBudgetService(IBudgetStore store, GetMonthlySpendByCategoryHandler spend, ITenantContext tenant, TimeProvider clock)
{
    /// <summary>The tenant's budgets for <paramref name="month"/> (null = the current month).</summary>
    public async Task<IReadOnlyList<EffectiveBudget>> ListAsync(DateOnly? month = null, CancellationToken ct = default)
    {
        var rows = await store.ListAsync(tenant.TenantId, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var current = new DateOnly(today.Year, today.Month, 1);
        var target = month is { } m ? new DateOnly(m.Year, m.Month, 1) : current;
        var key = target.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        Dictionary<string, decimal>? previous = null; // last month's spend, fetched only when some budget needs it
        var result = new List<EffectiveBudget>(rows.Count);
        foreach (var row in rows)
        {
            if (!row.Rollover || row.MonthlyLimit <= 0)
            {
                result.Add(new EffectiveBudget(row, 0m, row.MonthlyLimit, row.Version));
                continue;
            }

            if (row.CarriedForMonth == key)
            {
                result.Add(new EffectiveBudget(row, row.CarriedAmount, row.MonthlyLimit + row.CarriedAmount, row.Version));
                continue;
            }

            previous ??= await spend.HandleAsync(target.AddMonths(-1), ct);
            var carried = BudgetRollover.EffectiveLimit(row.MonthlyLimit, MonthlySpend.SpentFor(row.Category, previous), rollover: true) - row.MonthlyLimit;
            var version = row.Version;
            if (target == current)
            {
                // Only the current month is stored; looking at another month is a read and must not change anything.
                var saved = await store.SetCarriedAsync(tenant.TenantId, row.Category, carried, key, ct);
                if (saved != 0)
                {
                    version = saved;
                }
            }

            result.Add(new EffectiveBudget(row, carried, row.MonthlyLimit + carried, version));
        }

        return result;
    }
}
