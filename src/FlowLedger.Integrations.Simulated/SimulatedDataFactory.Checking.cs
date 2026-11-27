using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Simulated;

public static partial class SimulatedDataFactory
{
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
        var payroll = 3_400m + MoneyRounding.Round(faker.Random.Decimal(-200m, 200m), 2);
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
                var amount = -MoneyRounding.Round(faker.Random.Decimal(88m, 165m), 2);
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
            var amount = -MoneyRounding.Round(faker.Random.Decimal(95m, 230m), 2);
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
            var amount = -MoneyRounding.Round(faker.Random.Decimal(42m, 88m), 2);
            yield return Tx(accountId, faker.Random.AlphaNumeric(12), d, false,
                new Money(amount, "USD"), "FUEL STATION PURCHASE", "QuickFuel Stations", "Transport");
        }

        // ── Healthcare / copay (roughly once every 6 weeks) ───────────────
        for (var d = start; d <= today; d = d.AddDays(42))
        {
            var amount = -MoneyRounding.Round(faker.Random.Decimal(25m, 75m), 2);
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
            var amount = MoneyRounding.Round(faker.Random.Decimal(18m, 120m), 2);
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
}
