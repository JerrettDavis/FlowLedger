using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Simulated;

public static partial class SimulatedDataFactory
{
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
                var interest = MoneyRounding.Round(faker.Random.Decimal(42m, 88m), 2);
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
            var amount = -MoneyRounding.Round(faker.Random.Decimal(14m, 92m), 2);
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
                var amount = MoneyRounding.Round(faker.Random.Decimal(lo, hi), 2);
                yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                    new Money(amount, "USD"), desc, merchant, "Transport");
            }
        }

        // Online shopping (sporadic)
        var shopMerchants = new[] { "MarketNest Online", "TechDen Electronics", "GreenLeaf Garden Supply" };
        for (var d = start; d <= today; d = d.AddDays(faker.Random.Int(12, 25)))
        {
            var amount = -MoneyRounding.Round(faker.Random.Decimal(22m, 220m), 2);
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
}
