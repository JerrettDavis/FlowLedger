namespace FlowLedger.Integrations.Mx;

/// <summary>
/// MX account/transaction endpoints are scoped by <c>user_guid</c>, but the
/// <see cref="FlowLedger.Integrations.Abstractions.IFinancialDataProvider"/> contract passes only
/// a single opaque id back to the provider (the member id to <c>GetAccountsAsync</c>, the account
/// id to <c>GetTransactionsAsync</c>). To carry the owning user without leaking MX specifics into
/// the domain, the provider packs <c>userGuid</c> and the MX guid into one opaque token of the form
/// <c>{userGuid}|{guid}</c>. The domain treats it as an opaque string; only this provider decodes it.
/// </summary>
internal static class MxCompositeId
{
    private const char Separator = '|';

    public static string Pack(string userGuid, string guid)
    {
        if (userGuid.Contains(Separator))
        {
            throw new ArgumentException(
                $"userGuid must not contain the separator character '{Separator}'.", nameof(userGuid));
        }

        if (guid.Contains(Separator))
        {
            throw new ArgumentException(
                $"guid must not contain the separator character '{Separator}'.", nameof(guid));
        }

        return $"{userGuid}{Separator}{guid}";
    }

    public static (string UserGuid, string Guid) Unpack(string composite)
    {
        if (string.IsNullOrWhiteSpace(composite))
        {
            throw new ArgumentException("Composite MX id must not be empty.", nameof(composite));
        }

        var idx = composite.IndexOf(Separator);
        if (idx <= 0 || idx >= composite.Length - 1)
        {
            throw new FlowLedger.Integrations.Abstractions.FatalProviderException(
                $"Malformed MX composite id '{composite}'. Expected '{{userGuid}}{Separator}{{guid}}'.");
        }

        return (composite[..idx], composite[(idx + 1)..]);
    }
}
