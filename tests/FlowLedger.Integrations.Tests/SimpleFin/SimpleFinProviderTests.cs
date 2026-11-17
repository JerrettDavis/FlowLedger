using System.Net;
using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.SimpleFin;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.SimpleFin;

public sealed class SimpleFinProviderTests
{
    private static readonly long Today = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (SimpleFinFinancialDataProvider P, StubHandler H) Make(
        string body, HttpStatusCode status = HttpStatusCode.OK, string? accessUrl = "https://u:p@bridge.example/simplefin")
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bridge.example/simplefin/") };
        var opts = Options.Create(new SimpleFinOptions { Enabled = true, AccessUrl = accessUrl });
        return (new SimpleFinFinancialDataProvider(http, opts, NullLogger<SimpleFinFinancialDataProvider>.Instance), handler);
    }

    private static string Json(long posted) => $$"""
    {
      "errors": [],
      "accounts": [
        { "org": { "id": "arvest", "name": "Arvest Bank" }, "id": "chk1", "name": "Everyday Checking",
          "currency": "USD", "balance": "1200.50", "available-balance": "1100.00",
          "transactions": [
            { "id": "t1", "posted": {{posted}}, "amount": "-45.10", "description": "KROGER" },
            { "id": "t2", "posted": {{posted}}, "amount": "2000.00", "description": "PAYROLL" } ] },
        { "org": { "id": "visa", "name": "Arvest Visa Credit Card" }, "id": "cc1", "name": "Rewards",
          "currency": "USD", "balance": "-350.25", "transactions": [] }
      ]
    }
    """;

    [Fact]
    public async Task Maps_accounts_and_flips_liability_sign_to_owed()
    {
        var (p, _) = Make(Json(Today));
        var accounts = await p.GetAccountsAsync("simplefin");

        accounts.Should().HaveCount(2);
        accounts[0].AccountType.Should().Be("CHECKING");
        accounts[0].Balance.Amount.Should().Be(1200.50m);
        accounts[1].AccountType.Should().Be("CREDIT_CARD");
        accounts[1].Balance.Amount.Should().Be(350.25m);
    }

    [Fact]
    public async Task One_http_call_serves_accounts_and_all_transactions()
    {
        var (p, h) = Make(Json(Today));
        var accounts = await p.GetAccountsAsync("simplefin");
        var page = await p.GetTransactionsAsync(accounts[0].ProviderId, SyncCursor.Initial);

        h.Calls.Should().Be(1);
        h.LastUri!.Query.Should().Contain("start-date=").And.Contain("pending=1");
        page.Items.Should().HaveCount(2);
        page.Items[0].Amount.Amount.Should().Be(-45.10m);
        page.HasMore.Should().BeFalse();
        page.NextCursor.IsInitial.Should().BeFalse();
    }

    [Fact]
    public async Task Cursor_filters_older_transactions_beyond_overlap()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-40).ToUnixTimeSeconds();
        var (p, _) = Make(Json(old));
        var accounts = await p.GetAccountsAsync("simplefin");
        var cursor = new SyncCursor(DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"));

        var page = await p.GetTransactionsAsync(accounts[0].ProviderId, cursor);
        page.Items.Should().BeEmpty();
        page.NextCursor.Should().Be(cursor);
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Auth_failures_need_user_action(HttpStatusCode status)
    {
        var (p, _) = Make("{}", status);
        await p.Invoking(x => x.GetAccountsAsync("simplefin")).Should().ThrowAsync<NeedsUserActionProviderException>();
    }

    [Fact]
    public async Task Rate_limit_maps_to_rate_limited()
    {
        var (p, _) = Make("{}", HttpStatusCode.TooManyRequests);
        await p.Invoking(x => x.GetAccountsAsync("simplefin")).Should().ThrowAsync<RateLimitedProviderException>();
    }

    [Fact]
    public async Task Errors_with_no_accounts_need_user_action()
    {
        var (p, _) = Make("""{ "errors": ["Arvest needs attention"], "accounts": [] }""");
        await p.Invoking(x => x.GetAccountsAsync("simplefin")).Should().ThrowAsync<NeedsUserActionProviderException>();
    }

    [Fact]
    public async Task Missing_access_url_needs_user_action()
    {
        var (p, _) = Make("{}", accessUrl: null);
        await p.Invoking(x => x.GetAccountsAsync("simplefin")).Should().ThrowAsync<NeedsUserActionProviderException>();
    }

    [Theory]
    [InlineData("Arvest", "Home Mortgage", "MORTGAGE")]
    [InlineData("Arvest", "Auto Loan", "LOAN")]
    [InlineData("Chase", "Sapphire Visa", "CREDIT_CARD")]
    [InlineData("Arvest", "Premium Savings", "SAVINGS")]
    [InlineData("Fidelity", "Rollover IRA", "INVESTMENT")]
    [InlineData("Arvest", "Everyday Checking", "CHECKING")]
    [InlineData("Chase Bank JDHPro - Chase", "Chase Freedom (7156)", "CREDIT_CARD")]
    [InlineData("Citibank", "Citi Custom Cash Card-7029", "CREDIT_CARD")]
    [InlineData("Ameriprise Financial", "SIGNATURE WEALTH (7133)", "INVESTMENT")]
    [InlineData("Ameriprise Financial", "SPS ADVANTAGE (7133)", "INVESTMENT")]
    [InlineData("Arvest Bank", "Primay Checking (1219)", "CHECKING")]
    [InlineData("Miramar Credit Union", "Share Draft", "CHECKING")]
    [InlineData("Discover Bank", "Online Savings", "SAVINGS")]
    [InlineData("Discover Bank", "Cashback Debit Checking", "CHECKING")]
    [InlineData("Some Bank", "Rewards Checking", "CHECKING")]
    [InlineData("Discover Bank", "Discover it Card", "CREDIT_CARD")]
    [InlineData("Arvest Bank - Personal Credit Card", "Account 1234", "CREDIT_CARD")]
    public void Infers_account_type_from_names(string org, string name, string expected)
    {
        var acct = new SfAccount { Id = "x", Name = name, Org = new SfOrg { Name = org } };
        SimpleFinMapper.InferType(acct).Should().Be(expected);
    }
}
