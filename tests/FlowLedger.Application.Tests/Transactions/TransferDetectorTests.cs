using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Transactions;

public sealed class TransferDetectorTests
{
    private static readonly Guid Checking = Guid.NewGuid();
    private static readonly Guid Savings = Guid.NewGuid();
    private static readonly Guid Mortgage = Guid.NewGuid();
    private static readonly Guid Card = Guid.NewGuid();

    private static readonly Dictionary<Guid, string> Types = new()
    {
        [Checking] = "Checking",
        [Savings] = "Savings",
        [Mortgage] = "Mortgage",
        [Card] = "CreditCard",
    };

    private static TransactionDto Tx(Guid account, decimal amount, string dir, int day, string desc = "x", string status = "Posted") =>
        TransactionDtoBuilder.A()
            .OnAccount(account).WithAmount(amount).Described(desc).WithStatus(status)
            .On(new DateOnly(2026, 11, day))
            .With(b => dir == "Credit" ? b.Credit() : b.Debit())
            .Build();

    [Fact]
    public void Opposite_legs_on_different_accounts_are_a_transfer()
    {
        var debit = Tx(Checking, 600, "Debit", 3, "To savings");
        var credit = Tx(Savings, 600, "Credit", 3, "Internal Transfer");
        var coffee = Tx(Checking, 4.5m, "Debit", 3);

        var found = TransferDetector.Detect([debit, credit, coffee], Types);

        found.Should().BeEquivalentTo([debit.Id, credit.Id]);
    }

    [Fact]
    public void Legs_a_few_days_apart_still_match_but_a_week_apart_does_not()
    {
        var a = Tx(Checking, 500, "Debit", 1);
        var b = Tx(Savings, 500, "Credit", 3);
        var farDebit = Tx(Checking, 90, "Debit", 1);
        var farCredit = Tx(Savings, 90, "Credit", 9);

        var found = TransferDetector.Detect([a, b, farDebit, farCredit], Types);

        found.Should().BeEquivalentTo([a.Id, b.Id]);
    }

    [Fact]
    public void Same_account_or_different_amount_is_not_a_transfer()
    {
        var found = TransferDetector.Detect(
            [Tx(Checking, 50, "Debit", 1), Tx(Checking, 50, "Credit", 1), Tx(Checking, 51, "Debit", 1), Tx(Savings, 50, "Credit", 28)], Types);

        found.Should().BeEmpty();
    }

    [Fact]
    public void Mortgage_payment_recorded_as_debit_on_both_accounts_is_one_transfer()
    {
        var fromChecking = Tx(Checking, 2180, "Debit", 1, "Lakeview Mortgage Services");
        var onMortgage = Tx(Mortgage, 2180, "Debit", 1, "Lakeview Mortgage Services");

        var found = TransferDetector.Detect([fromChecking, onMortgage], Types);

        found.Should().BeEquivalentTo([fromChecking.Id, onMortgage.Id]);
    }

    [Fact]
    public void Two_unrelated_same_direction_debits_between_asset_accounts_are_not_matched()
    {
        var found = TransferDetector.Detect([Tx(Checking, 20, "Debit", 1, "A"), Tx(Savings, 20, "Debit", 1, "A")], Types);

        found.Should().BeEmpty();
    }

    [Fact]
    public void Matching_is_one_to_one()
    {
        var d1 = Tx(Checking, 100, "Debit", 1);
        var d2 = Tx(Checking, 100, "Debit", 2);
        var c1 = Tx(Savings, 100, "Credit", 1);

        var found = TransferDetector.Detect([d1, d2, c1], Types);

        found.Should().HaveCount(2, "one credit can only pair with one debit");
        found.Should().Contain(c1.Id);
    }

    [Fact]
    public void Planned_rows_are_ignored()
    {
        var found = TransferDetector.Detect([Tx(Checking, 10, "Debit", 1, status: "Planned"), Tx(Savings, 10, "Credit", 1)], Types);

        found.Should().BeEmpty();
    }
}
