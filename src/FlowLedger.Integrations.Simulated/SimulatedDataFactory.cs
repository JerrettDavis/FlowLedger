using Bogus;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Simulated;

/// <summary>
/// Factory that generates the deterministic demo household described in PLAN §24.
/// Determinism contract: same <paramref name="tenantId"/> + <paramref name="baseSeed"/>
/// always produces byte-identical output regardless of call order or thread.
///
/// Seeding strategy: each per-account Faker is seeded with
///   baseSeed XOR tenant.GetHashCode() XOR accountIndex
/// so different tenants receive distinct data while the data for a given tenant is stable.
/// </summary>
public static class SimulatedDataFactory
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

    private static IReadOnlyList<AccountDescriptor> BuildAccountDescriptors(
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
        var mortgageId = AccountId("mtg");

        var balFaker = MakeFaker(tenantId, baseSeed, slotIndex: 1);

        decimal CheckingBalance() => Math.Round(balFaker.Random.Decimal(1200m, 4800m), 2);
        decimal SavingsBalance() => Math.Round(balFaker.Random.Decimal(5000m, 25000m), 2);
        decimal CreditBalance() => -Math.Round(balFaker.Random.Decimal(400m, 2800m), 2);
        decimal MortgageBalance() => -Math.Round(balFaker.Random.Decimal(180_000m, 350_000m), 2);

        return new List<AccountDescriptor>
        {
            new(new ProviderAccount(checkingId, "Primary Checking", "CHECKING",
                new Money(CheckingBalance(), "USD"), new Money(CheckingBalance() - 50m, "USD"), "USD"),
                "CHECKING", 0),

            new(new ProviderAccount(savingsId, "High-Yield Savings", "SAVINGS",
                new Money(SavingsBalance(), "USD"), null, "USD"),
                "SAVINGS", 1),

            new(new ProviderAccount(creditId, "Everyday Visa", "CREDIT",
                new Money(CreditBalance(), "USD"), null, "USD"),
                "CREDIT", 2),

            new(new ProviderAccount(mortgageId, "Home Mortgage", "MORTGAGE",
                new Money(MortgageBalance(), "USD"), null, "USD"),
                "MORTGAGE", 3),
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
            "MORTGAGE" => GenerateMortgageTransactions(descriptor.Account.ProviderId, tenantId, baseSeed, historyMonths),
            _ => Enumerable.Empty<ProviderTransaction>(),
        };
    }

    private static IEnumerable<ProviderTransaction> GenerateCheckingTransactions(
        string accountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var faker = MakeFaker(tenantId, baseSeed, slotIndex: 10);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-historyMonths);

        // ── Payroll (bi-weekly Fridays) ───────────────────────────────────
        // Use a fixed epoch Friday so the bi-weekly schedule is stable regardless
        // of when the history window starts.
        var payroll = 3_400m + Math.Round(faker.Random.Decimal(-200m, 200m), 2);
        // Epoch: 2024-01-05 is a known Friday used as the bi-weekly anchor.
        var epochFriday = new DateOnly(2024, 1, 5);
        for (var d = start; d <= today; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Friday && ((d.DayNumber - epochFriday.DayNumber) % 14 == 0))
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                    new Money(payroll, "USD"), "PAYROLL DIRECT DEPOSIT", "Employer Payroll", null);
            }
        }

        // ── Rent / mortgage transfer ──────────────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var firstOfMonth = new DateOnly(d.Year, d.Month, 1);
            if (firstOfMonth >= start && firstOfMonth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), firstOfMonth, false,
                    new Money(-1_650m, "USD"), "MORTGAGE PAYMENT", "Mortgage Servicer", null);
            }
        }

        // ── Utilities (monthly, ~15th) ────────────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var fifteenth = new DateOnly(d.Year, d.Month, 15);
            if (fifteenth >= start && fifteenth <= today)
            {
                var amount = -Math.Round(faker.Random.Decimal(95m, 180m), 2);
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), fifteenth, false,
                    new Money(amount, "USD"), "ELECTRIC UTILITY BILL", "Power Company", "Utilities");
            }
        }

        // ── Insurance (monthly, ~5th) ─────────────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var fifth = new DateOnly(d.Year, d.Month, 5);
            if (fifth >= start && fifth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), fifth, false,
                    new Money(-220m, "USD"), "AUTO INSURANCE PAYMENT", "Insurance Co", "Insurance");
            }
        }

        // ── Groceries (weekly) ────────────────────────────────────────────
        for (var d = start; d <= today; d = d.AddDays(7))
        {
            var amount = -Math.Round(faker.Random.Decimal(80m, 240m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "GROCERY STORE PURCHASE", "Local Grocery", "Groceries");
        }

        // ── Fuel (every ~10 days) ─────────────────────────────────────────
        for (var d = start; d <= today; d = d.AddDays(10))
        {
            var amount = -Math.Round(faker.Random.Decimal(40m, 85m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "FUEL STATION PURCHASE", "Gas Station", "Fuel");
        }
    }

    private static IEnumerable<ProviderTransaction> GenerateSavingsTransactions(
        string accountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var faker = MakeFaker(tenantId, baseSeed, slotIndex: 11);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-historyMonths);

        // Monthly savings transfer from checking
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var third = new DateOnly(d.Year, d.Month, 3);
            if (third >= start && third <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), third, false,
                    new Money(500m, "USD"), "TRANSFER FROM CHECKING", "Internal Transfer", "Savings");
            }
        }

        // Monthly interest credit (~last day of month)
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var lastDay = new DateOnly(d.Year, d.Month,
                DateTime.DaysInMonth(d.Year, d.Month));
            if (lastDay >= start && lastDay <= today)
            {
                var interest = Math.Round(faker.Random.Decimal(8m, 35m), 2);
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), lastDay, false,
                    new Money(interest, "USD"), "INTEREST CREDIT", null, "Interest");
            }
        }
    }

    private static IEnumerable<ProviderTransaction> GenerateCreditTransactions(
        string accountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var faker = MakeFaker(tenantId, baseSeed, slotIndex: 12);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-historyMonths);

        // Subscriptions (monthly)
        var subscriptions = new[]
        {
            ("NETFLIX SUBSCRIPTION", "Netflix", -15.99m),
            ("SPOTIFY PREMIUM", "Spotify", -9.99m),
            ("AMAZON PRIME", "Amazon", -14.99m),
            ("CLOUD BACKUP SERVICE", "Backup Service", -6.99m),
        };

        foreach (var (desc, merchant, amount) in subscriptions)
        {
            var startDay = faker.Random.Int(1, 28);
            for (var d = start; d <= today; d = d.AddMonths(1))
            {
                var billingDay = new DateOnly(d.Year, d.Month,
                    Math.Min(startDay, DateTime.DaysInMonth(d.Year, d.Month)));
                if (billingDay >= start && billingDay <= today)
                {
                    yield return Tx(accountId, faker.Random.AlphaNumeric(12), billingDay, false,
                        new Money(amount, "USD"), desc, merchant, "Subscriptions");
                }
            }
        }

        // Restaurants / dining (random, ~3x per week)
        for (var d = start; d <= today; d = d.AddDays(faker.Random.Int(2, 4)))
        {
            var amount = -Math.Round(faker.Random.Decimal(12m, 85m), 2);
            var restaurant = faker.Company.CompanyName();
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), $"RESTAURANT {restaurant.ToUpperInvariant()}", restaurant, "Dining");
        }

        // Monthly payment (credit card autopay)
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var payDay = new DateOnly(d.Year, d.Month, 22);
            if (payDay >= start && payDay <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), payDay, false,
                    new Money(500m, "USD"), "AUTOPAY CREDIT CARD PAYMENT", "Card Issuer", "Payment");
            }
        }
    }

    private static IEnumerable<ProviderTransaction> GenerateMortgageTransactions(
        string accountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var faker = MakeFaker(tenantId, baseSeed, slotIndex: 13);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-historyMonths);

        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var payDay = new DateOnly(d.Year, d.Month, 1);
            if (payDay >= start && payDay <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), payDay, false,
                    new Money(-1_650m, "USD"), "MORTGAGE MONTHLY PAYMENT", "Mortgage Servicer", "Housing");
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProviderTransaction Tx(
        string accountId,
        string providerId,
        DateOnly postedDate,
        bool isPending,
        Money amount,
        string rawDescription,
        string? merchantName,
        string? category) =>
        new(providerId, accountId, postedDate, isPending,
            amount, rawDescription, merchantName, category);

    /// <summary>
    /// Creates a Bogus <see cref="Faker"/> with a fully deterministic seed derived from
    /// the tenant id and a slot index so each data category gets its own independent sequence.
    /// </summary>
    public static Faker MakeFaker(TenantId tenantId, int baseSeed, int slotIndex)
    {
        // XOR-mix ensures different tenants always get different data
        // while remaining stable for a given tenant + slotIndex pair.
        // Use all 16 bytes of the Guid to avoid collisions from masked hash codes.
        // Use unchecked arithmetic to avoid overflow; result is fully deterministic.
        var tenantBytes = tenantId.Value.ToByteArray();
        var tenantHash = 0;
        for (var i = 0; i < tenantBytes.Length; i++)
        {
            tenantHash = unchecked(tenantHash * 31 + tenantBytes[i]);
        }

        var seed = unchecked(baseSeed ^ tenantHash ^ (slotIndex * (int)0x9E3779B9));
        return new Faker("en") { Random = new Randomizer(seed) };
    }
}
