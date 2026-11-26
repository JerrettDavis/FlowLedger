namespace FlowLedger.Contracts;

/// <summary>Response DTO for a single account.</summary>
public sealed record AccountDto(
    Guid Id,
    string Name,
    string AccountType,
    decimal BalanceAmount,
    string BalanceCurrency,
    string? Institution,
    string? ExternalAccountRef,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastBalanceConfirmedAt,
    bool TypeLockedByUser = false,
    string Mode = "Standard",
    bool HideTransactions = false,
    uint Version = 0);

/// <summary>Request DTO for correcting an account's type by hand (pins it against provider sync).</summary>
public sealed record ChangeAccountTypeRequest(string AccountType);

/// <summary>Request DTO for creating an account.</summary>
public sealed record CreateAccountRequest(
    string Name,
    string AccountType,
    decimal StartingBalance,
    string Currency,
    string? Institution,
    decimal? CreditLimit);

/// <summary>
/// Tracking preferences. IsActive=false stops syncing and removes the account from every view; Mode=Holding keeps it for
/// balance/net worth/drill-down only; HideTransactions keeps its transactions out of everyday views.
/// </summary>
public sealed record AccountSettingsRequest(bool IsActive, string Mode, bool HideTransactions);

/// <summary>Request DTO for updating an account name.</summary>
public sealed record UpdateAccountRequest(string Name);
