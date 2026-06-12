using FlowLedger.Domain.ValueObjects;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.Simulated;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.Simulated;

/// <summary>
/// Verifies that the Simulated provider generates the demo household described in PLAN §24:
/// checking, savings, credit card, mortgage with expected transaction categories.
/// </summary>
public sealed class SimulatedSeedDataTests
{
    private static readonly TenantId TestTenant =
        TenantId.From(new Guid("00000000-0000-0000-0000-000000000001"));

    private static SimulatedFinancialDataProvider MakeProvider(int historyMonths = 6) =>
        new(Options.Create(new SimulatedProviderOptions { HistoryMonths = historyMonths }));

    [Fact]
    public async Task Demo_household_has_four_accounts()
    {
        var provider = MakeProvider();
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        accounts.Should().HaveCount(4);
    }

    [Fact]
    public async Task Demo_household_includes_checking_savings_credit_mortgage()
    {
        var provider = MakeProvider();
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        var types = accounts.Select(a => a.AccountType).ToHashSet();
        types.Should().Contain("CHECKING");
        types.Should().Contain("SAVINGS");
        types.Should().Contain("CREDIT");
        types.Should().Contain("MORTGAGE");
    }

    [Fact]
    public async Task Checking_account_has_payroll_transactions()
    {
        var provider = MakeProvider(historyMonths: 3);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var checking = accounts.Single(a => a.AccountType == "CHECKING");

        var transactions = await DrainAllAsync(provider, checking.ProviderId);

        transactions.Should().Contain(t =>
            t.RawDescription.Contains("PAYROLL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Checking_account_has_utility_transactions()
    {
        var provider = MakeProvider(historyMonths: 3);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var checking = accounts.Single(a => a.AccountType == "CHECKING");

        var transactions = await DrainAllAsync(provider, checking.ProviderId);

        transactions.Should().Contain(t =>
            t.RawDescription.Contains("UTILITY", StringComparison.OrdinalIgnoreCase) ||
            (t.MerchantName != null && t.MerchantName.Contains("Power", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Checking_account_has_grocery_transactions()
    {
        var provider = MakeProvider(historyMonths: 3);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var checking = accounts.Single(a => a.AccountType == "CHECKING");

        var transactions = await DrainAllAsync(provider, checking.ProviderId);

        transactions.Should().Contain(t =>
            t.RawDescription.Contains("GROCERY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Checking_account_has_fuel_transactions()
    {
        var provider = MakeProvider(historyMonths: 3);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var checking = accounts.Single(a => a.AccountType == "CHECKING");

        var transactions = await DrainAllAsync(provider, checking.ProviderId);

        transactions.Should().Contain(t =>
            t.RawDescription.Contains("FUEL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Credit_card_has_subscription_transactions()
    {
        var provider = MakeProvider(historyMonths: 3);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var credit = accounts.Single(a => a.AccountType == "CREDIT");

        var transactions = await DrainAllAsync(provider, credit.ProviderId);

        transactions.Should().Contain(t => t.ProviderCategory == "Subscriptions");
    }

    [Fact]
    public async Task Savings_account_has_interest_transactions()
    {
        var provider = MakeProvider(historyMonths: 3);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);
        var savings = accounts.Single(a => a.AccountType == "SAVINGS");

        var transactions = await DrainAllAsync(provider, savings.ProviderId);

        transactions.Should().Contain(t =>
            t.RawDescription.Contains("INTEREST", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task All_transaction_amounts_are_in_usd()
    {
        var provider = MakeProvider(historyMonths: 2);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        foreach (var account in accounts)
        {
            var transactions = await DrainAllAsync(provider, account.ProviderId);
            transactions.Should().AllSatisfy(t =>
                t.Amount.Currency.Code.Should().Be("USD"),
                because: "demo data is all USD");
        }
    }

    [Fact]
    public async Task Transactions_have_non_empty_raw_descriptions()
    {
        var provider = MakeProvider(historyMonths: 2);
        var member = await provider.BeginConnectionAsync(TestTenant);
        var accounts = await provider.GetAccountsAsync(member.ProviderId);

        foreach (var account in accounts)
        {
            var transactions = await DrainAllAsync(provider, account.ProviderId);
            transactions.Should().AllSatisfy(t =>
                t.RawDescription.Should().NotBeNullOrWhiteSpace());
        }
    }

    [Fact]
    public async Task History_months_controls_transaction_volume()
    {
        var providerFew = MakeProvider(historyMonths: 1);
        var providerMany = MakeProvider(historyMonths: 6);

        var memberFew = await providerFew.BeginConnectionAsync(TestTenant);
        var memberMany = await providerMany.BeginConnectionAsync(TestTenant);

        var accountsFew = await providerFew.GetAccountsAsync(memberFew.ProviderId);
        var accountsMany = await providerMany.GetAccountsAsync(memberMany.ProviderId);

        // Match by index (both providers produce same account types in same order)
        var txFew = await DrainAllAsync(providerFew, accountsFew[0].ProviderId);
        var txMany = await DrainAllAsync(providerMany, accountsMany[0].ProviderId);

        txMany.Count.Should().BeGreaterThan(txFew.Count,
            because: "more history months should produce more transactions");
    }

    private static async Task<List<ProviderTransaction>> DrainAllAsync(
        IFinancialDataProvider provider,
        string accountId)
    {
        var all = new List<ProviderTransaction>();
        var cursor = SyncCursor.Initial;

        do
        {
            var page = await provider.GetTransactionsAsync(accountId, cursor, pageSize: 100);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
            if (!page.HasMore) break;
        }
        while (true);

        return all;
    }
}
