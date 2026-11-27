using Bogus;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Simulated;

public static partial class SimulatedDataFactory
{
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
            var div = MoneyRounding.Round(faker.Random.Decimal(28m, 145m), 2);
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

    // ── Recurring flow seeds ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the canonical set of recurring-flow seeds for the demo household.
    /// These map directly to patterns visible in the generated transaction history
    /// (payroll, mortgage, utilities, subscriptions, insurance, etc.).
    ///
    /// All amounts are positive; the caller supplies <see cref="TransactionDirection"/>.
    /// <paramref name="checkingProviderAccountId"/> must be the provider account id for the
    /// Primary Checking account (used only so callers can correlate which domain account
    /// to attach these flows to — the factory does not resolve domain AccountIds).
    /// </summary>
    public static IReadOnlyList<RecurringFlowSeed> GetRecurringFlowSeeds(
        string checkingProviderAccountId,
        string creditProviderAccountId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        return new List<RecurringFlowSeed>
        {
            // ── Income ────────────────────────────────────────────────────────
            new(
                checkingProviderAccountId,
                "Payroll – Meridian Group LLC",
                3_400m,
                "USD",
                "Credit",
                "EveryNWeeks",
                IntervalWeeks: 2,
                AnchorDayOfWeek: "Friday",
                DayOfMonth: null,
                Counterparty: "Meridian Group LLC"),

            // ── Housing ───────────────────────────────────────────────────────
            new(
                checkingProviderAccountId,
                "Home Mortgage – Lakeview",
                2_180m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 1,
                Counterparty: "Lakeview Mortgage Services"),

            // ── Utilities ─────────────────────────────────────────────────────
            new(
                checkingProviderAccountId,
                "Electric – Lumen Utilities",
                127m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 15,
                Counterparty: "Lumen Utilities"),

            new(
                checkingProviderAccountId,
                "Internet – ClearWave",
                79.99m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 12,
                Counterparty: "ClearWave Internet"),

            // ── Insurance ─────────────────────────────────────────────────────
            new(
                checkingProviderAccountId,
                "Auto Insurance – Ridgeline",
                247m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 5,
                Counterparty: "Ridgeline Insurance"),

            new(
                checkingProviderAccountId,
                "Health Insurance Premium – BluePeak",
                185m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 2,
                Counterparty: "BluePeak Health"),

            // ── Subscriptions ─────────────────────────────────────────────────
            new(
                creditProviderAccountId,
                "Streaming Subscriptions",
                60.95m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 1,
                Counterparty: "Various Streaming Services"),

            // ── Savings & investments ─────────────────────────────────────────
            new(
                checkingProviderAccountId,
                "Savings Transfer – High-Yield",
                600m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 3,
                Counterparty: "High-Yield Savings"),

            new(
                checkingProviderAccountId,
                "Brokerage Contribution – Horizon",
                500m,
                "USD",
                "Debit",
                "Monthly",
                IntervalWeeks: null,
                AnchorDayOfWeek: null,
                DayOfMonth: 20,
                Counterparty: "Horizon Brokerage"),
        }.AsReadOnly();
    }
}
