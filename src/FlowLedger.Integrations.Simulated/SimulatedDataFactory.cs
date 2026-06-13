using Bogus;
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
        var apexCardId = AccountId("cc2");
        var brokerageId = AccountId("inv");
        var mortgageId = AccountId("mtg");

        var balFaker = MakeFaker(tenantId, baseSeed, slotIndex: 1);

        decimal CheckingBalance() => Math.Round(balFaker.Random.Decimal(2_400m, 5_800m), 2);
        decimal SavingsBalance() => Math.Round(balFaker.Random.Decimal(14_000m, 32_000m), 2);
        decimal CreditBalance() => -Math.Round(balFaker.Random.Decimal(380m, 1_800m), 2);
        decimal ApexBalance() => -Math.Round(balFaker.Random.Decimal(220m, 960m), 2);
        decimal BrokerageBalance() => Math.Round(balFaker.Random.Decimal(18_500m, 72_000m), 2);
        decimal MortgageBalance() => -Math.Round(balFaker.Random.Decimal(198_000m, 312_000m), 2);

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
                    new Money(payroll, "USD"), "PAYROLL DIRECT DEPOSIT", "Meridian Group LLC", "Payroll");
            }
        }

        // ── Mortgage payment ──────────────────────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var firstOfMonth = new DateOnly(d.Year, d.Month, 1);
            if (firstOfMonth >= start && firstOfMonth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), firstOfMonth, false,
                    new Money(-2_180m, "USD"), "MORTGAGE PAYMENT", "Lakeview Mortgage Services", "Housing");
            }
        }

        // ── Utilities — electric (monthly, ~15th) ─────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var fifteenth = new DateOnly(d.Year, d.Month, 15);
            if (fifteenth >= start && fifteenth <= today)
            {
                var amount = -Math.Round(faker.Random.Decimal(88m, 165m), 2);
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), fifteenth, false,
                    new Money(amount, "USD"), "ELECTRIC UTILITY BILL", "Lumen Utilities", "Utilities");
            }
        }

        // ── Utilities — internet (monthly, ~12th) ─────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var twelfth = new DateOnly(d.Year, d.Month, 12);
            if (twelfth >= start && twelfth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), twelfth, false,
                    new Money(-79.99m, "USD"), "INTERNET SERVICE", "ClearWave Internet", "Utilities");
            }
        }

        // ── Insurance (monthly, ~5th) ─────────────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var fifth = new DateOnly(d.Year, d.Month, 5);
            if (fifth >= start && fifth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), fifth, false,
                    new Money(-247m, "USD"), "AUTO INSURANCE PAYMENT", "Ridgeline Insurance", "Insurance");
            }
        }

        // ── Health insurance premium (monthly, ~1st) ──────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var second = new DateOnly(d.Year, d.Month, 2);
            if (second >= start && second <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), second, false,
                    new Money(-185m, "USD"), "HEALTH INSURANCE PREMIUM", "BluePeak Health", "Healthcare");
            }
        }

        // ── Groceries (weekly, Saturdays) ─────────────────────────────────
        for (var d = start; d <= today; d = d.AddDays(7))
        {
            var amount = -Math.Round(faker.Random.Decimal(95m, 230m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "GROCERY STORE PURCHASE", "Bluejay Grocery Co.", "Groceries");
        }

        // ── Transit pass (monthly) ────────────────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var eighth = new DateOnly(d.Year, d.Month, 8);
            if (eighth >= start && eighth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), eighth, false,
                    new Money(-98m, "USD"), "METRO TRANSIT MONTHLY PASS", "Metro Transit Authority", "Transport");
            }
        }

        // ── Fuel (every ~10 days) ─────────────────────────────────────────
        for (var d = start; d <= today; d = d.AddDays(10))
        {
            var amount = -Math.Round(faker.Random.Decimal(42m, 88m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "FUEL STATION PURCHASE", "QuickFuel Stations", "Transport");
        }

        // ── Healthcare / copay (roughly once every 6 weeks) ───────────────
        for (var d = start; d <= today; d = d.AddDays(42))
        {
            var amount = -Math.Round(faker.Random.Decimal(25m, 75m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "MEDICAL COPAY", "Clearview Medical Group", "Healthcare");
        }

        // ── Investment contribution (monthly, 20th) ───────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var twentieth = new DateOnly(d.Year, d.Month, 20);
            if (twentieth >= start && twentieth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), twentieth, false,
                    new Money(-500m, "USD"), "TRANSFER TO BROKERAGE", "Horizon Brokerage", "Investments");
            }
        }

        // ── Savings transfer (monthly, 3rd) ───────────────────────────────
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var third = new DateOnly(d.Year, d.Month, 3);
            if (third >= start && third <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), third, false,
                    new Money(-600m, "USD"), "TRANSFER TO SAVINGS", "High-Yield Savings", "Savings");
            }
        }

        // ── Occasional refund (roughly every 2 months) ────────────────────
        for (var d = start; d <= today; d = d.AddDays(61))
        {
            var amount = Math.Round(faker.Random.Decimal(18m, 120m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "MERCHANT REFUND", "Bluejay Grocery Co.", "Refunds");
        }

        // ── Low-water-mark scenario: large irregular expense (first month only) ──
        // This creates at least one overdraft-warning scenario for the forecast chart.
        var lowWaterDate = new DateOnly(start.Year, start.Month, 28);
        if (lowWaterDate >= start && lowWaterDate <= today)
        {
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), lowWaterDate, false,
                new Money(-1_850m, "USD"), "HOME REPAIR - PLUMBING", "Riverstone Plumbing & Repairs", "Home Maintenance");
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

        // Monthly savings transfer from checking (3rd of each month)
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var third = new DateOnly(d.Year, d.Month, 3);
            if (third >= start && third <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), third, false,
                    new Money(600m, "USD"), "TRANSFER FROM CHECKING", "Internal Transfer", "Savings");
            }
        }

        // Monthly interest credit (~last day of month) at high-yield rate
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var lastDay = new DateOnly(d.Year, d.Month,
                DateTime.DaysInMonth(d.Year, d.Month));
            if (lastDay >= start && lastDay <= today)
            {
                var interest = Math.Round(faker.Random.Decimal(42m, 88m), 2);
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), lastDay, false,
                    new Money(interest, "USD"), "INTEREST CREDIT", null, "Interest");
            }
        }

        // Goal contribution top-up (quarterly) — makes goals visible in UI
        for (var d = start; d <= today; d = d.AddMonths(3))
        {
            var fifteenth = new DateOnly(d.Year, d.Month, 15);
            if (fifteenth >= start && fifteenth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), fifteenth, false,
                    new Money(1_000m, "USD"), "VACATION FUND CONTRIBUTION", "Internal Transfer", "Goals");
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
            ("STREAMFLIX SUBSCRIPTION",  "StreamFlix",         -15.99m),
            ("SOUNDWAVE PREMIUM",         "SoundWave Music",    -9.99m),
            ("SHOPSWIFT PRIME",           "ShopSwift",         -14.99m),
            ("CLOUDVAULT BACKUP",         "CloudVault Storage",  -6.99m),
            ("FITTRACK MEMBERSHIP",       "FitTrack Health",   -12.99m),
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

        // Restaurants / dining (random, ~3×/week) using clearly-synthetic names
        var restaurants = new[]
        {
            "The Copper Kettle Bistro",
            "Hawthorn Noodle Bar",
            "Bluestone Tacos",
            "Mapletree Sushi",
            "Ember & Rye Grill",
            "Saltwater Poke Co.",
            "Ironwood Pizza Kitchen",
            "Clover Leaf Cafe",
        };
        for (var d = start; d <= today; d = d.AddDays(faker.Random.Int(2, 4)))
        {
            var amount = -Math.Round(faker.Random.Decimal(14m, 92m), 2);
            var restaurant = restaurants[faker.Random.Int(0, restaurants.Length - 1)];
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), $"RESTAURANT {restaurant.ToUpperInvariant()}", restaurant, "Dining");
        }

        // Monthly payment (credit card autopay, 22nd)
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var payDay = new DateOnly(d.Year, d.Month, 22);
            if (payDay >= start && payDay <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), payDay, false,
                    new Money(650m, "USD"), "AUTOPAY CREDIT CARD PAYMENT", "Everyday Visa", "Payment");
            }
        }
    }

    private static IEnumerable<ProviderTransaction> GenerateApexCardTransactions(
        string accountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var faker = MakeFaker(tenantId, baseSeed, slotIndex: 14);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-historyMonths);

        // Travel / transport on rewards card
        var travelMerchants = new[]
        {
            ("RIDESHARE APEX",    "Apex Rideshare",     -18m,  -55m),
            ("PARKING CITYPARK",  "CityPark Garages",   -12m,  -38m),
            ("AIRPORT SHUTTLE",   "Swiftway Shuttle",   -22m,  -65m),
        };

        foreach (var (desc, merchant, lo, hi) in travelMerchants)
        {
            for (var d = start; d <= today; d = d.AddDays(faker.Random.Int(7, 14)))
            {
                var amount = Math.Round(faker.Random.Decimal(lo, hi), 2);
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                    new Money(amount, "USD"), desc, merchant, "Transport");
            }
        }

        // Online shopping (sporadic)
        var shopMerchants = new[] { "MarketNest Online", "TechDen Electronics", "GreenLeaf Garden Supply" };
        for (var d = start; d <= today; d = d.AddDays(faker.Random.Int(12, 25)))
        {
            var amount = -Math.Round(faker.Random.Decimal(22m, 220m), 2);
            var merchant = shopMerchants[faker.Random.Int(0, shopMerchants.Length - 1)];
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), $"ONLINE PURCHASE {merchant.ToUpperInvariant()}", merchant, "Shopping");
        }

        // Monthly payment (15th)
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var payDay = new DateOnly(d.Year, d.Month, 15);
            if (payDay >= start && payDay <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), payDay, false,
                    new Money(400m, "USD"), "AUTOPAY APEX REWARDS PAYMENT", "Apex Rewards Card", "Payment");
            }
        }
    }

    private static IEnumerable<ProviderTransaction> GenerateInvestmentTransactions(
        string accountId,
        TenantId tenantId,
        int baseSeed,
        int historyMonths)
    {
        var faker = MakeFaker(tenantId, baseSeed, slotIndex: 15);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = today.AddMonths(-historyMonths);

        // Monthly contributions (20th) from checking
        for (var d = start; d <= today; d = d.AddMonths(1))
        {
            var twentieth = new DateOnly(d.Year, d.Month, 20);
            if (twentieth >= start && twentieth <= today)
            {
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), twentieth, false,
                    new Money(500m, "USD"), "INVESTMENT CONTRIBUTION", "Internal Transfer", "Investments");
            }
        }

        // Quarterly dividend reinvestment
        for (var d = start; d <= today; d = d.AddMonths(3))
        {
            var div = Math.Round(faker.Random.Decimal(28m, 145m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(div, "USD"), "DIVIDEND REINVESTMENT", "Horizon Brokerage", "Investment Income");
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
                    new Money(-2_180m, "USD"), "MORTGAGE MONTHLY PAYMENT", "Lakeview Mortgage Services", "Housing");
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
