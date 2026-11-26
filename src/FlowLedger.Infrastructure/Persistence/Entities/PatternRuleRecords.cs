namespace FlowLedger.Infrastructure.Persistence.Entities;

/// <summary>A merchant-pattern to display-alias rule (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class MerchantAliasRecord
{
    public Guid TenantId { get; private set; }
    public string Pattern { get; private set; } = string.Empty;
    public string Alias { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; private set; }

    private MerchantAliasRecord() { }
}

/// <summary>A pattern to category-path categorization rule (#154: formerly an ad-hoc Npgsql table).</summary>
internal sealed class CategoryRuleRecord
{
    public Guid TenantId { get; private set; }
    public string Pattern { get; private set; } = string.Empty;
    public string CategoryPath { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedUtc { get; private set; }

    private CategoryRuleRecord() { }
}
