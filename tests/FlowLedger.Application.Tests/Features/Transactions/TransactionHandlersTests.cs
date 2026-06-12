using FlowLedger.Application.Features.Transactions;
using FlowLedger.Application.Tests.Fakes;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Transactions;

public sealed class TransactionHandlersTests
{
    private readonly FakeTransactionRepository _repo = new();
    private readonly FakeTenantContext _tenant = new();

    private CreateTransactionRequest ValidRequest(Guid? accountId = null) => new(
        accountId ?? Guid.NewGuid(),
        150m,
        "USD",
        "Debit",
        "Grocery Store",
        new DateOnly(2026, 6, 1),
        new DateOnly(2026, 6, 1),
        null,
        null,
        null);

    // ── CreateTransactionHandler ──────────────────────────────────────────────

    [Fact]
    public async Task CreateTransaction_ValidRequest_StoresAndReturnsDto()
    {
        var handler = new CreateTransactionHandler(_repo, _tenant);
        var dto = await handler.HandleAsync(ValidRequest());

        dto.Amount.Should().Be(150m);
        dto.Currency.Should().Be("USD");
        dto.Direction.Should().Be("Debit");
        dto.Description.Should().Be("Grocery Store");
        dto.Status.Should().Be("Posted");
        dto.Source.Should().Be("Manual");
    }

    [Fact]
    public async Task CreateTransaction_CreditDirection_Stored()
    {
        var handler = new CreateTransactionHandler(_repo, _tenant);
        var request = ValidRequest() with { Direction = "Credit" };
        var dto = await handler.HandleAsync(request);
        dto.Direction.Should().Be("Credit");
    }

    // ── GetTransactionHandler ─────────────────────────────────────────────────

    [Fact]
    public async Task GetTransaction_ExistingId_ReturnsDto()
    {
        var createHandler = new CreateTransactionHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(ValidRequest());

        var getHandler = new GetTransactionHandler(_repo);
        var dto = await getHandler.HandleAsync(created.Id);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetTransaction_UnknownId_ReturnsNull()
    {
        var handler = new GetTransactionHandler(_repo);
        var result = await handler.HandleAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    // ── ListTransactionsHandler ───────────────────────────────────────────────

    [Fact]
    public async Task ListTransactions_ReturnsAll()
    {
        var createHandler = new CreateTransactionHandler(_repo, _tenant);
        await createHandler.HandleAsync(ValidRequest());
        await createHandler.HandleAsync(ValidRequest());

        var listHandler = new ListTransactionsHandler(_repo);
        var results = await listHandler.HandleAsync(new ListTransactionsQuery());

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListTransactions_FilterByAccount_ReturnsMatchingOnly()
    {
        var acctA = Guid.NewGuid();
        var acctB = Guid.NewGuid();

        var createHandler = new CreateTransactionHandler(_repo, _tenant);
        await createHandler.HandleAsync(ValidRequest(acctA));
        await createHandler.HandleAsync(ValidRequest(acctA));
        await createHandler.HandleAsync(ValidRequest(acctB));

        var listHandler = new ListTransactionsHandler(_repo);
        var results = await listHandler.HandleAsync(new ListTransactionsQuery(AccountId: acctA));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(t => t.AccountId == acctA);
    }
}
