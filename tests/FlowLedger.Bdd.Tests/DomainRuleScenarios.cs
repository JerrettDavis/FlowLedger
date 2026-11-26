using FlowLedger.Application.Features.Debt;
using FlowLedger.Application.Features.Imports;
using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport.Builders;
using TinyBDD;
using TinyBDD.Xunit;
using Xunit.Abstractions;

namespace FlowLedger.Bdd.Tests;

/// <summary>
/// Acceptance scenarios for the pure business rules a user relies on (debt payoff order, bank-export sign conventions, transfer
/// detection). They run through the real application services with no database, so unlike the Postgres-backed scenarios they
/// execute on every machine, including CI runners without Docker (#161).
/// </summary>
[Feature("Money rules that need no database")]
public partial class DomainRuleScenarios(ITestOutputHelper output) : TinyBddXunitBase(output)
{
    private static readonly DebtInput[] CardAndLoan =
    [
        new("Card", 1000m, 24m, 25m),
        new("Loan", 500m, 6m, 25m),
    ];

    // ── Debt payoff ───────────────────────────────────────────────────────────

    [Scenario("Paying the highest-rate debt first costs less interest than paying the smallest first"), Fact, DisableOptimization]
    public async Task Avalanche_beats_snowball_on_interest()
    {
        await Given("a 24% card and a 6% loan with 100 a month extra", () => 100m)
            .When("both strategies are planned", extra => (
                Avalanche: DebtPayoffPlanner.Plan(CardAndLoan, extra, DebtPayoffPlanner.Avalanche),
                Snowball: DebtPayoffPlanner.Plan(CardAndLoan, extra, DebtPayoffPlanner.Snowball)))
            .Then("both are feasible", plans => plans.Avalanche.Feasible && plans.Snowball.Feasible)
            .And("avalanche pays strictly less interest", plans => plans.Avalanche.TotalInterest < plans.Snowball.TotalInterest)
            .AssertPassed();
    }

    [Scenario("Paying extra each month shortens the payoff and the interest"), Fact, DisableOptimization]
    public async Task Extra_payment_shortens_payoff()
    {
        await Given("a card and a loan on minimum payments", () => CardAndLoan)
            .When("an extra 200 a month is added", debts => (
                Slow: DebtPayoffPlanner.Plan(debts, 0m, DebtPayoffPlanner.Avalanche),
                Fast: DebtPayoffPlanner.Plan(debts, 200m, DebtPayoffPlanner.Avalanche)))
            .Then("the debt is cleared in fewer months", plans => plans.Fast.Months < plans.Slow.Months)
            .And("less interest is paid in total", plans => plans.Fast.TotalInterest < plans.Slow.TotalInterest)
            .AssertPassed();
    }

    [Scenario("A payment smaller than the interest is reported as never paying off"), Fact, DisableOptimization]
    public async Task Payment_below_interest_is_infeasible()
    {
        await Given("a 10,000 debt at 30% with a 50 a month payment", () => new DebtInput("Hole", 10000m, 30m, 50m))
            .When("it is planned", debt => DebtPayoffPlanner.Plan([debt], 0m, DebtPayoffPlanner.Avalanche))
            .Then("the plan says it is not feasible", plan => !plan.Feasible)
            .AssertPassed();
    }

    // ── Importing bank and app exports ────────────────────────────────────────

    private static List<List<string>> Rows(NormalizedImport n) => CsvParser.Parse(n.Csv).Skip(1).ToList();

    [Scenario("A Mint export is recognised and its debits become negative amounts"), Fact, DisableOptimization]
    public async Task Mint_export_signs_come_from_the_transaction_type()
    {
        const string mint =
            "\"Date\",\"Description\",\"Original Description\",\"Amount\",\"Transaction Type\",\"Category\",\"Account Name\",\"Labels\",\"Notes\"\n" +
            "\"1/15/2024\",\"Starbucks\",\"STARBUCKS #123\",\"4.50\",\"debit\",\"Coffee\",\"Checking\",\"\",\"\"\n" +
            "\"1/16/2024\",\"Paycheck\",\"ACME PAYROLL\",\"2,000.00\",\"credit\",\"Income\",\"Checking\",\"\",\"\"\n";

        await Given("a Mint export with a coffee and a paycheck", () => mint)
            .When("it is normalised", csv => ImportNormalizer.Normalize(csv))
            .Then("the format is detected as Mint", n => n.FormatId == "mint")
            .And("the coffee is an expense", n => Rows(n)[0][1] == "-4.50")
            .And("the paycheck is income", n => Rows(n)[1][1] == "2000.00")
            .AssertPassed();
    }

    [Scenario("An Amex export is recognised and charges are inverted into expenses"), Fact, DisableOptimization]
    public async Task Amex_export_charges_become_expenses()
    {
        const string amex = "Date,Receipt,Description,Amount\n01/05/2024,,UBER TRIP,23.40\n01/06/2024,,PAYMENT THANK YOU,-500.00\n";

        await Given("an Amex export with a charge and a payment", () => amex)
            .When("it is normalised", csv => ImportNormalizer.Normalize(csv))
            .Then("the format is detected as Amex", n => n.FormatId == "amex")
            .And("the charge is negative", n => Rows(n)[0][1] == "-23.40")
            .And("the payment is positive", n => Rows(n)[1][1] == "500.00")
            .AssertPassed();
    }

    [Scenario("A Monarch export keeps its signs and maps merchant and category"), Fact, DisableOptimization]
    public async Task Monarch_export_keeps_negative_expenses()
    {
        const string monarch = "Date,Merchant,Category,Account,Original Statement,Notes,Amount,Tags\n2024-02-01,Netflix,Entertainment,Visa,NETFLIX.COM,,-15.49,\n";

        await Given("a Monarch export with one subscription", () => monarch)
            .When("it is normalised", csv => ImportNormalizer.Normalize(csv))
            .Then("the format is detected as Monarch", n => n.FormatId == "monarch")
            .And("the row carries date, amount, statement text, merchant and category",
                n => Rows(n)[0].SequenceEqual(["2024-02-01", "-15.49", "NETFLIX.COM", "Netflix", "Entertainment"]))
            .AssertPassed();
    }

    // ── Transfers between own accounts ────────────────────────────────────────

    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly Guid Savings = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> AccountTypes = new() { [Checking] = "Checking", [Savings] = "Savings" };

    private static TransactionDto Debit(Guid account, decimal amount, int day) =>
        TransactionDtoBuilder.A().OnAccount(account).WithAmount(amount).Debit().On(new DateOnly(2026, 11, day)).Build();

    private static TransactionDto Credit(Guid account, decimal amount, int day) =>
        TransactionDtoBuilder.A().OnAccount(account).WithAmount(amount).Credit().On(new DateOnly(2026, 11, day)).Build();

    [Scenario("Matching legs on two of my accounts are recognised as a transfer, not spending"), Fact, DisableOptimization]
    public async Task Opposite_legs_are_a_transfer()
    {
        var out600 = Debit(Checking, 600m, 3);
        var in600 = Credit(Savings, 600m, 3);
        var coffee = Debit(Checking, 4.5m, 3);

        await Given("a 600 debit from checking, a 600 credit to savings and a coffee on the same day", () => new[] { out600, in600, coffee })
            .When("transfers are detected", txns => TransferDetector.Detect(txns, AccountTypes))
            .Then("both 600 legs are transfers", found => found.Contains(out600.Id) && found.Contains(in600.Id))
            .And("the coffee is still spending", found => !found.Contains(coffee.Id))
            .AssertPassed();
    }

    [Scenario("Legs a week apart are two unrelated transactions"), Fact, DisableOptimization]
    public async Task Legs_a_week_apart_are_not_a_transfer()
    {
        var out90 = Debit(Checking, 90m, 1);
        var in90 = Credit(Savings, 90m, 9);

        await Given("a 90 debit on the 1st and a 90 credit on the 9th", () => new[] { out90, in90 })
            .When("transfers are detected", txns => TransferDetector.Detect(txns, AccountTypes))
            .Then("neither is treated as a transfer", found => found.Count == 0)
            .AssertPassed();
    }

    [Scenario("A refund on the same account is not a transfer"), Fact, DisableOptimization]
    public async Task Same_account_legs_are_not_a_transfer()
    {
        var spend = Debit(Checking, 50m, 1);
        var refund = Credit(Checking, 50m, 1);

        await Given("a 50 debit and a 50 credit on the same account", () => new[] { spend, refund })
            .When("transfers are detected", txns => TransferDetector.Detect(txns, AccountTypes))
            .Then("neither is treated as a transfer", found => found.Count == 0)
            .AssertPassed();
    }
}
