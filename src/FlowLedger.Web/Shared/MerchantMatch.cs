namespace FlowLedger.Web.Shared;

/// <summary>Estimates how many other loaded transactions a "same merchant" bulk recategorisation would touch.</summary>
public static class MerchantMatch
{
    public static string Key(TransactionDto t) =>
        (string.IsNullOrWhiteSpace(t.MerchantName) ? t.Description : t.MerchantName).Trim();

    /// <summary>Other transactions in <paramref name="loaded"/> from the same merchant as <paramref name="tx"/>.</summary>
    public static int CountOthers(IEnumerable<TransactionDto> loaded, TransactionDto tx)
    {
        var key = Key(tx);
        return loaded.Count(o => o.Id != tx.Id && string.Equals(Key(o), key, StringComparison.OrdinalIgnoreCase));
    }
}
