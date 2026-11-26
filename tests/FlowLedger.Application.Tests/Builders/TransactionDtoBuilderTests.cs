using FlowLedger.Application.Features.Transactions;
using FlowLedger.TestSupport;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Builders;

public sealed class TransactionDtoBuilderTests
{
    [Fact]
    public void Defaults_are_a_posted_usd_debit_on_the_fixed_default_date()
    {
        TransactionDto dto = TransactionDtoBuilder.A();

        dto.Status.Should().Be("Posted");
        dto.Direction.Should().Be("Debit");
        dto.Currency.Should().Be("USD");
        dto.EffectiveDate.Should().Be(TransactionDtoBuilder.DefaultDate);
        dto.PostedDate.Should().Be(TransactionDtoBuilder.DefaultDate);
    }

    [Fact]
    public void Overrides_apply_and_unposted_leaves_posted_date_null()
    {
        var account = Guid.NewGuid();
        var dto = TransactionDtoBuilder.A().OnAccount(account).WithAmount(42.5m).Credit().Unposted(new DateOnly(2026, 5, 31)).Build();

        dto.AccountId.Should().Be(account);
        dto.Amount.Should().Be(42.5m);
        dto.Direction.Should().Be("Credit");
        dto.EffectiveDate.Should().Be(new DateOnly(2026, 5, 31));
        dto.PostedDate.Should().BeNull();
    }

    [Fact]
    public void Seeded_fakes_are_identical_on_every_run()
    {
        Fake.Merchant(7).Should().Be(Fake.Merchant(7));
        Fake.Merchants(5, seed: 3).Should().Equal(Fake.Merchants(5, seed: 3));
    }
}
