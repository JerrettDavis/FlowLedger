using FlowLedger.Domain.Aggregates;

namespace FlowLedger.Application.Features.Accounts;

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
    DateTimeOffset? LastBalanceConfirmedAt);

/// <summary>Request DTO for creating an account.</summary>
public sealed record CreateAccountRequest(
    string Name,
    string AccountType,
    decimal StartingBalance,
    string Currency,
    string? Institution,
    decimal? CreditLimit);

/// <summary>Request DTO for updating an account name.</summary>
public sealed record UpdateAccountRequest(string Name);
