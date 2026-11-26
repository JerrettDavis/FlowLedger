namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A monthly spending limit for one category path (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class BudgetRecord
{
    public Guid TenantId { get; private set; }
    public string Category { get; private set; } = string.Empty;
    public decimal MonthlyLimit { get; private set; }
    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>Optimistic-concurrency token: Postgres <c>xmin</c> (#157).</summary>
    public uint RowVersion { get; private set; }

    private BudgetRecord() { }
}
