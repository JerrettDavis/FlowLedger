using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Accounts;

public sealed class AccountHandlersTests
{
    private readonly FakeAccountRepository _repo = new();
    private readonly FakeTenantContext _tenant = new();

    // ── CreateAccountHandler ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateAccount_ValidRequest_ReturnsDto()
    {
        var handler = new CreateAccountHandler(_repo, _tenant);
        var request = CreateAccountRequestBuilder.A().StartingAt(1000m).At("Chase").Build();

        var dto = await handler.HandleAsync(request);

        dto.Name.Should().Be("Checking");
        dto.BalanceAmount.Should().Be(1000m);
        dto.BalanceCurrency.Should().Be("USD");
        dto.AccountType.Should().Be("Checking");
        dto.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAccount_CreditCard_StoresBalance()
    {
        var handler = new CreateAccountHandler(_repo, _tenant);
        var request = CreateAccountRequestBuilder.A().Named("Visa").OfType("CreditCard").StartingAt(0m).WithCreditLimit(5000m).Build();

        var dto = await handler.HandleAsync(request);

        dto.AccountType.Should().Be("CreditCard");
        dto.BalanceAmount.Should().Be(0m);
    }

    // ── GetAccountHandler ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccount_ExistingId_ReturnsDto()
    {
        var createHandler = new CreateAccountHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(CreateAccountRequestBuilder.A().Named("Savings").OfType("Savings").StartingAt(5000m).Build());

        var getHandler = new GetAccountHandler(_repo);
        var dto = await getHandler.HandleAsync(created.Id);

        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Savings");
    }

    [Fact]
    public async Task GetAccount_UnknownId_ReturnsNull()
    {
        var handler = new GetAccountHandler(_repo);
        var dto = await handler.HandleAsync(Guid.NewGuid());
        dto.Should().BeNull();
    }

    // ── ListAccountsHandler ───────────────────────────────────────────────────

    [Fact]
    public async Task ListAccounts_MultipleAccounts_ReturnsAll()
    {
        var createHandler = new CreateAccountHandler(_repo, _tenant);
        await createHandler.HandleAsync(CreateAccountRequestBuilder.A().Named("A1").StartingAt(100m).Build());
        await createHandler.HandleAsync(CreateAccountRequestBuilder.A().Named("A2").OfType("Savings").StartingAt(200m).Build());

        var listHandler = new ListAccountsHandler(_repo);
        var accounts = await listHandler.HandleAsync();

        accounts.Should().HaveCount(2);
    }

    // ── UpdateAccountHandler ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAccount_ExistingId_RenamesAccount()
    {
        var createHandler = new CreateAccountHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(CreateAccountRequestBuilder.A().Named("Old Name").StartingAt(0m).Build());

        var updateHandler = new UpdateAccountHandler(_repo);
        var updated = await updateHandler.HandleAsync(created.Id, new UpdateAccountRequest("New Name"));

        updated.Should().NotBeNull();
        updated!.Name.Should().Be("New Name");
    }

    [Fact]
    public async Task UpdateAccount_UnknownId_ReturnsNull()
    {
        var handler = new UpdateAccountHandler(_repo);
        var result = await handler.HandleAsync(Guid.NewGuid(), new UpdateAccountRequest("X"));
        result.Should().BeNull();
    }

    // ── DeactivateAccountHandler ──────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAccount_ExistingId_DeactivatesAndRemovedFromList()
    {
        var createHandler = new CreateAccountHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(CreateAccountRequestBuilder.A().Named("ToDeactivate").StartingAt(0m).Build());

        var deactivateHandler = new DeactivateAccountHandler(_repo);
        var found = await deactivateHandler.HandleAsync(created.Id);

        found.Should().BeTrue();

        // Should no longer appear in active list
        var listHandler = new ListAccountsHandler(_repo);
        var accounts = await listHandler.HandleAsync();
        accounts.Should().NotContain(a => a.Id == created.Id);
    }

    [Fact]
    public async Task DeactivateAccount_UnknownId_ReturnsFalse()
    {
        var handler = new DeactivateAccountHandler(_repo);
        var result = await handler.HandleAsync(Guid.NewGuid());
        result.Should().BeFalse();
    }
}
