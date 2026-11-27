namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A monthly spending limit for one category path (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class BudgetRecord
{
    public Guid TenantId { get; private set; }
    public string Category { get; private set; } = string.Empty;
    public decimal MonthlyLimit { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>When true, what was left unspent last month is added to this month's limit (#70).</summary>
    public bool Rollover { get; private set; }

    /// <summary>The amount carried into <see cref="CarriedForMonth"/> (zero when rollover is off or nothing was left).</summary>
    public decimal CarriedAmount { get; private set; }

    /// <summary>The month (<c>yyyy-MM</c>) <see cref="CarriedAmount"/> was worked out for; null when it has not been yet or went stale.</summary>
    public string? CarriedForMonth { get; private set; }

    /// <summary>Optimistic-concurrency token: Postgres <c>xmin</c> (#157).</summary>
    public uint RowVersion { get; private set; }

    private BudgetRecord() { }

    public static BudgetRecord Create(Guid tenantId, string category, decimal monthlyLimit, bool rollover = false) =>
        new() { TenantId = tenantId, Category = category, MonthlyLimit = monthlyLimit, Rollover = rollover, UpdatedUtc = DateTimeOffset.UtcNow };

    public void SetLimit(decimal monthlyLimit)
    {
        if (monthlyLimit != MonthlyLimit)
        {
            ClearCarried(); // the carried amount was worked out against the old limit
        }

        MonthlyLimit = monthlyLimit;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }

    public void SetRollover(bool rollover)
    {
        if (rollover != Rollover)
        {
            Rollover = rollover;
            ClearCarried();
        }
    }

    public void SetCarried(decimal amount, string month)
    {
        CarriedAmount = amount;
        CarriedForMonth = month;
    }

    private void ClearCarried()
    {
        CarriedAmount = 0m;
        CarriedForMonth = null;
    }
}
