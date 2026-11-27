using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Simulated;

/// <summary>
/// Factory that generates the deterministic demo household described in PLAN §24.
/// Determinism contract: same <paramref name="tenantId"/> + <paramref name="baseSeed"/>
/// always produces byte-identical output regardless of call order or thread.
///
/// Demo household (v2 — richer):
///   6 accounts: Primary Checking, High-Yield Savings, Everyday Visa, Apex Rewards Card,
///               Horizon Brokerage, Home Mortgage
///   Multi-month history across payroll, housing, utilities, groceries, dining, transport,
///   subscriptions, insurance, healthcare, savings transfers, investment contributions,
///   and occasional refunds — enough to produce compelling charts and low-water-mark forecasts.
///
/// Seeding strategy: each per-account Faker is seeded with
///   baseSeed XOR tenant.GetHashCode() XOR (slotIndex * 0x9E3779B9)
/// so different tenants receive distinct data while the data for a given tenant is stable.
/// </summary>
public static partial class SimulatedDataFactory
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the fixed set of provider accounts for the demo household.</summary>
    public static IReadOnlyList<ProviderAccount> GetAccounts(TenantId tenantId, int baseSeed)
    {
        return BuildAccountDescriptors(tenantId, baseSeed)
            .Select(d => d.Account)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Returns ALL transactions for a given provider account id.</summary>
    public static IReadOnlyList<ProviderTransaction> GetTransactions(
        string providerAccountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var descriptor = BuildAccountDescriptors(tenantId, baseSeed)
            .FirstOrDefault(d => d.Account.ProviderId == providerAccountId);

        if (descriptor is null)
        {
            return Array.Empty<ProviderTransaction>();
        }

        return GenerateTransactions(descriptor, tenantId, baseSeed, historyMonths)
            .OrderBy(t => t.PostedDate)
            .ToList()
            .AsReadOnly();
    }

    // ── Internal types ────────────────────────────────────────────────────────

    private sealed record AccountDescriptor(
        ProviderAccount Account,
        string AccountType,
        int AccountIndex);

    // ── Account generation ────────────────────────────────────────────────────

    private static List<AccountDescriptor> BuildAccountDescriptors(
        TenantId tenantId,
        int baseSeed)
    {
        // Stable account identifiers derived entirely from tenantId + baseSeed.
        var idFaker = MakeFaker(tenantId, baseSeed, slotIndex: 0);

        string AccountId(string prefix) => $"sim-{prefix}-{idFaker.Random.AlphaNumeric(8)}";

        // Rebuild deterministically every call — no mutable state.
        var checkingId = AccountId("chk");
        var savingsId = AccountId("sav");
        var creditId = AccountId("cc");
        var apexCardId = AccountId("cc2");
        var brokerageId = AccountId("inv");
        var mortgageId = AccountId("mtg");

        var balFaker = MakeFaker(tenantId, baseSeed, slotIndex: 1);

        decimal CheckingBalance() => MoneyRounding.Round(balFaker.Random.Decimal(2_400m, 5_800m), 2);
        decimal SavingsBalance() => MoneyRounding.Round(balFaker.Random.Decimal(14_000m, 32_000m), 2);
        decimal CreditBalance() => -MoneyRounding.Round(balFaker.Random.Decimal(380m, 1_800m), 2);
        decimal ApexBalance() => -MoneyRounding.Round(balFaker.Random.Decimal(220m, 960m), 2);
        decimal BrokerageBalance() => MoneyRounding.Round(balFaker.Random.Decimal(18_500m, 72_000m), 2);
        decimal MortgageBalance() => -MoneyRounding.Round(balFaker.Random.Decimal(198_000m, 312_000m), 2);

        var chkBal = CheckingBalance();
        var savBal = SavingsBalance();
        var ccBal = CreditBalance();
        var apxBal = ApexBalance();
        var invBal = BrokerageBalance();
        var mtgBal = MortgageBalance();

        return new List<AccountDescriptor>
        {
            new(new ProviderAccount(checkingId, "Primary Checking", "CHECKING",
                new Money(chkBal, "USD"), new Money(chkBal - 75m, "USD"), "USD"),
                "CHECKING", 0),

            new(new ProviderAccount(savingsId, "High-Yield Savings", "SAVINGS",
                new Money(savBal, "USD"), null, "USD"),
                "SAVINGS", 1),

            new(new ProviderAccount(creditId, "Everyday Visa", "CREDIT",
                new Money(ccBal, "USD"), null, "USD"),
                "CREDIT", 2),

            new(new ProviderAccount(apexCardId, "Apex Rewards Card", "CREDIT",
                new Money(apxBal, "USD"), null, "USD"),
                "CREDIT2", 3),

            new(new ProviderAccount(brokerageId, "Horizon Brokerage", "INVESTMENT",
                new Money(invBal, "USD"), null, "USD"),
                "INVESTMENT", 4),

            new(new ProviderAccount(mortgageId, "Home Mortgage", "MORTGAGE",
                new Money(mtgBal, "USD"), null, "USD"),
                "MORTGAGE", 5),
        };
    }

    // ── Transaction generation ────────────────────────────────────────────────

    private static IEnumerable<ProviderTransaction> GenerateTransactions(
        AccountDescriptor descriptor,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        return descriptor.AccountType switch
        {
            "CHECKING" => GenerateCheckingTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            "SAVINGS" => GenerateSavingsTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            "CREDIT" => GenerateCreditTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            "CREDIT2" => GenerateApexCardTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            "INVESTMENT" => GenerateInvestmentTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            "MORTGAGE" => GenerateMortgageTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            _ => Enumerable.Empty<ProviderTransaction>(),
        };
    }
}
