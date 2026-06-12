using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Tests.Fakes;
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
        var request = new CreateAccountRequest("Checking", "Checking", 1000m, "USD", "Chase", null);

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
        var request = new CreateAccountRequest("Visa", "CreditCard", 0m, "USD", null, 5000m);

        var dto = await handler.HandleAsync(request);

        dto.AccountType.Should().Be("CreditCard");
        dto.BalanceAmount.Should().Be(0m);
    }

    // ── GetAccountHandler ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccount_ExistingId_ReturnsDto()
    {
        var createHandler = new CreateAccountHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(new CreateAccountRequest("Savings", "Savings", 5000m, "USD", null, null));

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
        await createHandler.HandleAsync(new CreateAccountRequest("A1", "Checking", 100m, "USD", null, null));
        await createHandler.HandleAsync(new CreateAccountRequest("A2", "Savings", 200m, "USD", null, null));

        var listHandler = new ListAccountsHandler(_repo);
        var accounts = await listHandler.HandleAsync();

        accounts.Should().HaveCount(2);
    }

    // ── UpdateAccountHandler ──────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAccount_ExistingId_RenamesAccount()
    {
        var createHandler = new CreateAccountHandler(_repo, _tenant);
        var created = await createHandler.HandleAsync(new CreateAccountRequest("Old Name", "Checking", 0m, "USD", null, null));

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
        var created = await createHandler.HandleAsync(new CreateAccountRequest("ToDeactivate", "Checking", 0m, "USD", null, null));

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
