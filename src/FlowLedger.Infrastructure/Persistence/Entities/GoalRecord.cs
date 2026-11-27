namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A savings goal (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class GoalRecord
{
    public Guid TenantId { get; private set; }
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal TargetAmount { get; private set; }
    public decimal SavedAmount { get; private set; }
    public DateOnly? TargetDate { get; private set; }

    /// <summary>The account the goal is tracked against, if any (#70). A DB-level composite foreign key keeps it inside the tenant.</summary>
    public Guid? LinkedAccountId { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>Optimistic-concurrency token: Postgres <c>xmin</c> (#157).</summary>
    public uint RowVersion { get; private set; }

    private GoalRecord() { }

    public static GoalRecord Create(Guid tenantId, Guid id, string name, decimal targetAmount, decimal savedAmount, DateOnly? targetDate, Guid? linkedAccountId = null) =>
        new()
        {
            TenantId = tenantId,
            Id = id,
            Name = name,
            TargetAmount = targetAmount,
            SavedAmount = savedAmount,
            TargetDate = targetDate,
            LinkedAccountId = linkedAccountId,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

    public void Update(string name, decimal targetAmount, decimal savedAmount, DateOnly? targetDate, Guid? linkedAccountId = null)
    {
        Name = name;
        TargetAmount = targetAmount;
        SavedAmount = savedAmount;
        TargetDate = targetDate;
        LinkedAccountId = linkedAccountId;
        UpdatedUtc = DateTimeOffset.UtcNow;
    }
}
