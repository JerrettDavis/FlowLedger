using FlowLedger.Application.Features.Goals;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Goals;

/// <summary>#70: the 90-day balance trend of the account a goal is linked to.</summary>
public sealed class LinkedAccountTrendTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);
    private static readonly Guid Account = Guid.NewGuid();

    private static TransactionDto Tx(bool credit, decimal amount, int daysAgo, Guid? account = null, string status = "Posted")
    {
        var b = TransactionDtoBuilder.A().OnAccount(account ?? Account).WithAmount(amount).WithStatus(status).On(Today.AddDays(-daysAgo));
        return (credit ? b.Credit() : b.Debit()).Build();
    }

    [Fact]
    public void The_trend_is_the_accounts_net_change_over_90_days_per_month()
    {
        TransactionDto[] history = [Tx(true, 1000m, 80), Tx(false, 100m, 50), Tx(true, 400m, 10), Tx(true, 5000m, 120)]; // the 120-day-old credit is outside the window

        LinkedAccountTrend.MonthlyChange(Today, Account, history).Should().Be((1000m - 100m + 400m) / 3);
    }

    [Fact]
    public void Only_the_linked_accounts_own_transactions_count()
    {
        TransactionDto[] history = [Tx(true, 900m, 70), Tx(true, 9999m, 20, Guid.NewGuid()), Tx(false, 300m, 5)];

        LinkedAccountTrend.MonthlyChange(Today, Account, history).Should().Be(200m);
    }

    [Fact]
    public void Planned_transactions_are_ignored()
    {
        TransactionDto[] history = [Tx(true, 900m, 70), Tx(true, 3000m, 2, status: "Planned")];

        LinkedAccountTrend.MonthlyChange(Today, Account, history).Should().Be(300m);
    }

    [Fact]
    public void Too_little_history_on_the_account_claims_no_trend()
    {
        LinkedAccountTrend.MonthlyChange(Today, Account, []).Should().BeNull();
        LinkedAccountTrend.MonthlyChange(Today, Account, [Tx(true, 900m, 40), Tx(false, 10m, 5)]).Should().BeNull();
        LinkedAccountTrend.MonthlyChange(Today, Account, [Tx(true, 900m, 100, Guid.NewGuid())]).Should().BeNull("history of other accounts says nothing about this one");
    }
}
