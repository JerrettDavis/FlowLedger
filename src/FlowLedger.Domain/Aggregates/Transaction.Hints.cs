namespace FlowLedger.Domain.Aggregates;

public sealed partial class Transaction
{
    /// <summary>The category text the provider sent with the transaction (e.g. "Food &amp; Dining"). A hint for categorization only; never shown as the user's category.</summary>
    public string? ProviderCategory { get; private set; }

    /// <summary>Longest stored provider category (matches the column size).</summary>
    public const int MaxProviderCategoryLength = 120;

    /// <summary>Shared by both legs of a detected transfer between the user's own accounts (null when the transaction is not a known transfer leg).</summary>
    public Guid? TransferLinkId { get; private set; }

    /// <summary>Records the provider's own category as a categorization hint.</summary>
    public void SetProviderCategory(string? providerCategory)
    {
        var value = string.IsNullOrWhiteSpace(providerCategory) ? null : providerCategory.Trim();
        ProviderCategory = value is { Length: > MaxProviderCategoryLength } ? value[..MaxProviderCategoryLength] : value;
    }

    /// <summary>Links this transaction to the opposite leg of a transfer (both legs get the same id). Pass null to unlink.</summary>
    public bool LinkTransfer(Guid? transferLinkId)
    {
        if (TransferLinkId == transferLinkId)
        {
            return false;
        }

        TransferLinkId = transferLinkId;
        return true;
    }
}
