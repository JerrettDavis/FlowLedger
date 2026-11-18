using FlowLedger.Application.Features.RecurringFlows;
using FluentAssertions;

namespace FlowLedger.Application.Tests.RecurringFlows;

public sealed class RecurringFlowAliasMergeTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    private static IEnumerable<FlowTxn> Monthly(Guid account, string text, decimal amount, int day, int months) =>
        Enumerable.Range(1, months).Select(i => new FlowTxn(account, new DateOnly(2026, 11, day).AddMonths(-i), amount, false, text));

    [Fact]
    public void Same_bill_under_two_spellings_on_one_account_is_one_flow()
    {
        var acct = Guid.NewGuid();
        var txns = Monthly(acct, "Adobe CC", 69.99m, 12, 6).Concat(Monthly(acct, "ADOBE INC ADOBE", 69.99m, 12, 6)).ToList();

        var found = RecurringFlowDetector.Detect(txns, Today);

        found.Should().ContainSingle();
    }

    [Fact]
    public void Same_amount_on_a_different_day_or_account_is_not_merged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var txns = Monthly(a, "Streamly", 9.99m, 5, 6)
            .Concat(Monthly(a, "Musicly", 9.99m, 20, 6))
            .Concat(Monthly(b, "Videoly", 9.99m, 5, 6)).ToList();

        RecurringFlowDetector.Detect(txns, Today).Should().HaveCount(3);
    }
}
