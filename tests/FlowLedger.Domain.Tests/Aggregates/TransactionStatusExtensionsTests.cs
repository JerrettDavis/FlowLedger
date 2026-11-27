using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FluentAssertions;

namespace FlowLedger.Domain.Tests.Aggregates;

/// <summary>Pins the set of "real" (ledger-affecting) statuses used by every Application-layer filter (#106).</summary>
public sealed class TransactionStatusExtensionsTests
{
    [Fact]
    public void Only_planned_and_skipped_rows_are_not_real()
    {
        var real = Enum.GetValues<TransactionStatus>().Where(s => s.IsReal()).ToList();

        real.Should().BeEquivalentTo(new[]
        {
            TransactionStatus.Pending, TransactionStatus.Posted, TransactionStatus.Matched,
            TransactionStatus.Reconciled, TransactionStatus.Ignored, TransactionStatus.NeedsReview,
        });
    }

    [Theory]
    [InlineData("Planned", false)]
    [InlineData("Skipped", false)]
    [InlineData("Posted", true)]
    [InlineData("Pending", true)]
    [InlineData("Reconciled", true)]
    [InlineData("SomethingNew", true)]
    public void String_form_matches_the_enum_form_and_treats_unknown_values_as_real(string status, bool expected)
        => TransactionStatusExtensions.IsRealStatus(status).Should().Be(expected);

    [Fact]
    public void Expression_form_agrees_with_the_method_form_for_planned_skipped_and_actual_rows()
    {
        var tenant = TenantId.New();
        var account = AccountId.New();
        var money = new Money(5m, Currency.Usd);
        var today = new DateOnly(2026, 1, 1);
        var planned = Transaction.CreatePlanned(tenant, account, money, TransactionDirection.Debit, "Rent", today, FlowLedger.TestSupport.TestClock.Provider);
        var skipped = Transaction.CreatePlanned(tenant, account, money, TransactionDirection.Debit, "Rent", today, FlowLedger.TestSupport.TestClock.Provider);
        skipped.Skip();
        var posted = Transaction.RecordActual(tenant, account, money, TransactionDirection.Debit, "Coffee", today, today, TransactionSource.Manual, FlowLedger.TestSupport.TestClock.Provider);

        var predicate = TransactionStatusExtensions.RealTransaction.Compile();

        foreach (var t in new[] { planned, skipped, posted })
        {
            predicate(t).Should().Be(t.Status.IsReal(), $"status {t.Status}");
        }
    }
}
