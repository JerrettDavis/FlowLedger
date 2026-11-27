using FlowLedger.Application.Features.Transactions;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Transactions;

/// <summary>The Transactions page requests its full window (5000 rows); the API must accept exactly that and no more.</summary>
public sealed class ListTransactionsQueryValidatorTests
{
    private readonly ListTransactionsQueryValidator _validator = new();

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(5000)]
    public void Accepts_take_up_to_5000(int take) =>
        _validator.Validate(new ListTransactionsQuery(Take: take)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void Rejects_take_outside_range(int take) =>
        _validator.Validate(new ListTransactionsQuery(Take: take)).IsValid.Should().BeFalse();

    [Theory]
    [InlineData("date", "asc")]
    [InlineData("MERCHANT", "desc")]
    [InlineData("description", null)]
    [InlineData("amount", "ASC")]
    [InlineData(null, null)]
    public void Accepts_the_known_sort_keys(string? sort, string? dir) =>
        _validator.Validate(new ListTransactionsQuery(Sort: sort, Dir: dir)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("balance", null)]
    [InlineData("date; drop table", null)]
    [InlineData("date", "sideways")]
    public void Rejects_unknown_sort_keys_and_directions(string sort, string? dir) =>
        _validator.Validate(new ListTransactionsQuery(Sort: sort, Dir: dir)).IsValid.Should().BeFalse();

    [Fact]
    public void Sort_defaults_to_newest_first()
    {
        var query = new ListTransactionsQuery();

        query.SortKey.Should().Be(FlowLedger.Application.Abstractions.TransactionSortKey.Date);
        query.Descending.Should().BeTrue();
        new ListTransactionsQuery(Sort: "amount", Dir: "asc").SortKey.Should().Be(FlowLedger.Application.Abstractions.TransactionSortKey.Amount);
        new ListTransactionsQuery(Sort: "amount", Dir: "asc").Descending.Should().BeFalse();
    }

    [Fact]
    public void Column_filters_are_parsed_and_default_to_none()
    {
        new ListTransactionsQuery().Filter.Should().BeNull();

        var filter = new ListTransactionsQuery(Status: "pending", MinAmount: 5m, MaxAmount: 50m, Uncategorized: true).Filter;

        filter.Should().NotBeNull();
        filter!.Status.Should().Be(FlowLedger.Domain.Aggregates.TransactionStatus.Pending);
        filter.UncategorizedOnly.Should().BeTrue();
        (filter.MinAmount, filter.MaxAmount).Should().Be((5m, 50m));
    }

    [Fact]
    public void Rejects_an_unknown_status_and_an_inverted_amount_range()
    {
        _validator.Validate(new ListTransactionsQuery(Status: "bogus")).IsValid.Should().BeFalse();
        _validator.Validate(new ListTransactionsQuery(MinAmount: 50m, MaxAmount: 5m)).IsValid.Should().BeFalse();
        _validator.Validate(new ListTransactionsQuery(Status: "Posted", MinAmount: 5m, MaxAmount: 50m)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_handler_passes_the_filter_to_the_repository_for_the_page_and_the_count()
    {
        var repo = new FlowLedger.Application.Tests.Fakes.FakeTransactionRepository();
        var tenant = FlowLedger.Domain.ValueObjects.TenantId.New();
        var account = FlowLedger.Domain.ValueObjects.AccountId.New();
        var date = new DateOnly(2026, 3, 1);
        await repo.AddAsync(FlowLedger.Domain.Aggregates.Transaction.RecordActual(tenant, account, new FlowLedger.Domain.ValueObjects.Money(9m, "USD"),
            FlowLedger.Domain.Aggregates.TransactionDirection.Debit, "Small", date, date, FlowLedger.Domain.Aggregates.TransactionSource.Manual));
        await repo.AddAsync(FlowLedger.Domain.Aggregates.Transaction.RecordActual(tenant, account, new FlowLedger.Domain.ValueObjects.Money(90m, "USD"),
            FlowLedger.Domain.Aggregates.TransactionDirection.Debit, "Big", date, date, FlowLedger.Domain.Aggregates.TransactionSource.Manual));
        var handler = new ListTransactionsHandler(repo);
        var query = new ListTransactionsQuery(MinAmount: 50m, Uncategorized: true);

        (await handler.HandleAsync(query)).Select(t => t.Description).Should().Equal("Big");
        (await handler.CountAsync(query)).Should().Be(1);
    }
}
