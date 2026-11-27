using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Tests.Fakes;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.TestSupport.Builders;
using FluentAssertions;

namespace FlowLedger.Application.Tests.Features.Accounts;

public sealed class AccountSettingsTests
{
    private readonly FakeTenantContext _tenant = new();
    private readonly FakeAccountRepository _repo = new();

    private async Task<Account> AddAsync(string name, AccountType type)
    {
        var a = AccountBuilder.A().ForTenant(_tenant.TenantId).Named(name).OfType(type).Holding(10m).Build();
        await _repo.AddAsync(a);
        return a;
    }

    [Fact]
    public async Task Settings_update_persists_mode_hide_and_enabled()
    {
        var a = await AddAsync("Chk", AccountType.Checking);
        var dto = await new UpdateAccountSettingsHandler(_repo).HandleAsync(a.Id, AccountSettingsRequestBuilder.A().Active(true).InMode("holding").HidingTransactions().Build());

        dto!.Mode.Should().Be("Holding");
        dto.HideTransactions.Should().BeTrue();
        a.TransactionsHidden.Should().BeTrue();
    }

    [Fact]
    public async Task Settings_for_unknown_account_returns_null()
    {
        (await new UpdateAccountSettingsHandler(_repo).HandleAsync(Guid.NewGuid(), AccountSettingsRequestBuilder.A().Active(true).InMode("Standard").Build()))
            .Should().BeNull();
    }

    [Fact]
    public async Task Disabled_accounts_only_listed_when_requested()
    {
        var a = await AddAsync("Old", AccountType.Savings);
        await AddAsync("Chk", AccountType.Checking);
        await new UpdateAccountSettingsHandler(_repo).HandleAsync(a.Id, AccountSettingsRequestBuilder.A().Active(false).InMode("Standard").Build());

        var list = new ListAccountsHandler(_repo);
        (await list.HandleAsync()).Should().HaveCount(1);
        (await list.HandleAsync(true)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Excluded_ids_cover_disabled_and_hidden_unless_explicitly_selected()
    {
        var chk = await AddAsync("Chk", AccountType.Checking);
        var inv = await AddAsync("Brokerage", AccountType.Investment);
        var off = await AddAsync("Off", AccountType.Savings);
        off.ApplySettings(false, AccountMode.Standard, false);

        var excluded = await AccountVisibility.ExcludedIdsAsync(_repo);
        excluded.Should().BeEquivalentTo([inv.Id, off.Id]);
        excluded.Should().NotContain(chk.Id);

        var withSelection = await AccountVisibility.ExcludedIdsAsync(_repo, new HashSet<Guid> { inv.Id, off.Id });
        withSelection.Should().BeEquivalentTo([off.Id]);
    }
}
