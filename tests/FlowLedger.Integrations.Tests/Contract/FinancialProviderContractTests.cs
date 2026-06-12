using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;

namespace FlowLedger.Integrations.Tests.Contract;

/// <summary>
/// Abstract contract-test base class.  Any <see cref="IFinancialDataProvider"/> implementation
/// MUST pass every test declared here.
///
/// Subclasses create the provider under test by implementing <see cref="CreateProvider"/>
/// and may override <see cref="GetTestTenantId"/> to supply a deterministic tenant.
/// </summary>
public abstract class FinancialProviderContractTests
{
    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>Creates the provider under test.  Called once per test method.</summary>
    protected abstract IFinancialDataProvider CreateProvider();

    /// <summary>Returns a stable tenant id used in all contract tests.</summary>
    protected virtual TenantId GetTestTenantId() =>
        TenantId.From(new Guid("00000000-0000-0000-0000-000000000001"));

    // ── Provider metadata ─────────────────────────────────────────────────────

    [Fact]
    public void Provider_name_is_not_null_or_whitespace()
    {
        var provider = CreateProvider();
        provider.ProviderName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Capabilities_is_not_None_unless_deliberate()
    {
        // At minimum a real provider should support accounts or transactions.
        var provider = CreateProvider();
        provider.Capabilities.Should().NotBe(ProviderCapabilities.None,
            because: "a provider with no capabilities is not useful");
    }

    // ── Connection lifecycle ───────────────────────────────────────────────────

    [Fact]
    public async Task BeginConnection_returns_member_ref_with_non_empty_provider_id()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());

        member.ProviderId.Should().NotBeNullOrWhiteSpace();
        member.InstitutionName.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task BeginConnection_status_is_pending_or_connected()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());

        member.Status.Should().BeOneOf(ConnectionStatus.ConnectionPending, ConnectionStatus.Connected);
    }

    [Fact]
    public async Task GetConnectionStatus_returns_valid_enum_value()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());

        var status = await provider.GetConnectionStatusAsync(member.ProviderId);

        Enum.IsDefined(status).Should().BeTrue();
    }

    // ── Accounts ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccounts_returns_at_least_one_account()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());

        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        accounts.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetAccounts_all_have_non_empty_provider_ids()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        accounts.Should().AllSatisfy(a =>
            a.ProviderId.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task GetAccounts_provider_ids_are_unique()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        var ids = accounts.Select(a => a.ProviderId).ToList();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetAccounts_balances_use_money_not_floating_point()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        // Money uses decimal — verify Currency.Code is a 3-letter ISO code
        accounts.Should().AllSatisfy(a =>
        {
            a.Balance.Should().NotBeNull();
            a.Balance.Currency.Code.Should().MatchRegex(@"^[A-Z]{3}$");
        });
    }

    // ── Transaction pagination contract ───────────────────────────────────────

    [Fact]
    public async Task GetTransactions_pagination_terminates()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        // Use first account; all providers must have at least one
        var accountId = accounts[0].ProviderId;

        var cursor = SyncCursor.Initial;
        var pagesVisited = 0;
        bool hasMore;

        do
        {
            var page = await provider.GetTransactionsAsync(accountId, cursor, pageSize: 25);
            cursor = page.NextCursor;
            hasMore = page.HasMore;
            pagesVisited++;

            // Guard against infinite loop in a broken provider
            pagesVisited.Should().BeLessThan(1_000,
                because: "pagination must terminate");
        }
        while (hasMore);

        pagesVisited.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetTransactions_cursor_advances_each_page()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var accountId = accounts[0].ProviderId;

        var page1 = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 10);
        if (!page1.HasMore)
        {
            return; // Only one page — cursor advancement not testable; skip
        }

        var page2 = await provider.GetTransactionsAsync(accountId, page1.NextCursor, pageSize: 10);

        page1.NextCursor.Value.Should().NotBe(SyncCursor.Initial.Value,
            because: "cursor must advance past initial position");
        page1.NextCursor.Value.Should().NotBe(page2.NextCursor.Value,
            because: "cursor must advance between pages");
    }

    [Fact]
    public async Task GetTransactions_cursor_is_stable_on_re_fetch()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var accountId = accounts[0].ProviderId;

        var pageA = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 10);
        var pageB = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 10);

        pageA.NextCursor.Value.Should().Be(pageB.NextCursor.Value,
            because: "same cursor + same inputs must produce the same next cursor");
        pageA.Items.Should().HaveCount(pageB.Items.Count);
    }

    [Fact]
    public async Task GetTransactions_amounts_are_money_with_correct_currency()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var accountId = accounts[0].ProviderId;

        var page = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 50);

        page.Items.Should().AllSatisfy(t =>
        {
            t.Amount.Should().NotBeNull();
            t.Amount.Currency.Code.Should().MatchRegex(@"^[A-Z]{3}$");
        });
    }

    [Fact]
    public async Task GetTransactions_all_items_have_non_empty_account_id()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var accountId = accounts[0].ProviderId;

        var page = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 50);

        page.Items.Should().AllSatisfy(t =>
            t.ProviderAccountId.Should().NotBeNullOrWhiteSpace());
    }

    // ── Fingerprint stability ─────────────────────────────────────────────────

    [Fact]
    public async Task Transaction_fingerprint_inputs_are_stable_across_re_fetch()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var accountId = accounts[0].ProviderId;

        // Fetch twice; same cursor start
        var fetch1 = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 20);
        var fetch2 = await provider.GetTransactionsAsync(accountId, SyncCursor.Initial, pageSize: 20);

        fetch1.Items.Should().HaveCount(fetch2.Items.Count);

        for (var i = 0; i < fetch1.Items.Count; i++)
        {
            var t1 = fetch1.Items[i];
            var t2 = fetch2.Items[i];

            // All fingerprint input fields must be identical on re-fetch
            t1.ProviderId.Should().Be(t2.ProviderId);
            t1.ProviderAccountId.Should().Be(t2.ProviderAccountId);
            t1.PostedDate.Should().Be(t2.PostedDate);
            t1.Amount.Amount.Should().Be(t2.Amount.Amount);
            t1.Amount.Currency.Should().Be(t2.Amount.Currency);
            t1.RawDescription.Should().Be(t2.RawDescription);
            t1.IsPending.Should().Be(t2.IsPending);
        }
    }

    // ── Connection status transitions ─────────────────────────────────────────

    [Fact]
    public async Task Connection_status_is_a_defined_enum_value()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var status = await provider.GetConnectionStatusAsync(member.ProviderId);

        Enum.IsDefined(typeof(ConnectionStatus), status).Should().BeTrue();
    }

    [Fact]
    public async Task Connection_status_after_begin_is_not_error()
    {
        var provider = CreateProvider();
        var member = await provider.BeginConnectionAsync(GetTestTenantId());
        var status = await provider.GetConnectionStatusAsync(member.ProviderId);

        status.Should().NotBe(ConnectionStatus.Error,
            because: "a fresh connection should not immediately be in an error state");
    }
}
