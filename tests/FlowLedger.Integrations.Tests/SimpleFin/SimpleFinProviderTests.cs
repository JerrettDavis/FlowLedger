using System.Globalization;
using System.Net;
using System.Text;
using FlowLedger.Integrations.Abstractions;
using FlowLedger.Integrations.SimpleFin;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Integrations.Tests.SimpleFin;

public sealed class SimpleFinProviderTests
{
    private static readonly long Today = FlowLedger.TestSupport.TestClock.Instant.ToUnixTimeSeconds();

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
        return (new SimpleFinFinancialDataProvider(http, opts, NullLogger<SimpleFinFinancialDataProvider>.Instance, FlowLedger.TestSupport.TestClock.Provider), handler);
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

    private static SimpleFinFinancialDataProvider MakeWith(StubHandler handler, SimpleFinOptions options)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bridge.example/simplefin/") };
        return new SimpleFinFinancialDataProvider(http, Options.Create(options), NullLogger<SimpleFinFinancialDataProvider>.Instance, FlowLedger.TestSupport.TestClock.Provider);
    }

    [Fact]
    public async Task Deep_backfill_runs_once_and_the_cursor_marker_stops_it_on_later_syncs()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Json(Today));
        var options = new SimpleFinOptions { Enabled = true, AccessUrl = "https://u:p@bridge.example/simplefin", BackfillDays = 730 };

        var first = MakeWith(handler, options);
        var accounts = await first.GetAccountsAsync("simplefin");
        var page = await first.GetTransactionsAsync(accounts[0].ProviderId, SyncCursor.Initial);
        var callsAfterFirstSync = handler.Calls;
        callsAfterFirstSync.Should().BeGreaterThan(2, "the first sync walks the backfill windows");
        page.NextCursor.Value.Should().EndWith("|bf730", "the cursor records that the backfill completed");

        // Next sync (a new scoped provider) with the persisted cursor: only the one base request, no backfill.
        var second = MakeWith(handler, options);
        var accounts2 = await second.GetAccountsAsync("simplefin");
        var page2 = await second.GetTransactionsAsync(accounts2[0].ProviderId, page.NextCursor);

        (handler.Calls - callsAfterFirstSync).Should().Be(1);
        page2.NextCursor.Value.Should().EndWith("|bf730");
    }

    private sealed class GatedHandler(string body) : HttpMessageHandler
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            await Gate.Task.ConfigureAwait(false); // deliberately does not observe ct: the shared fetch must not depend on one caller's token
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_cancelled_first_caller_does_not_poison_the_shared_fetch()
    {
        var handler = new GatedHandler(Json(Today));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bridge.example/simplefin/") };
        var p = new SimpleFinFinancialDataProvider(http,
            Options.Create(new SimpleFinOptions { Enabled = true, AccessUrl = "https://u:p@bridge.example/simplefin" }),
            NullLogger<SimpleFinFinancialDataProvider>.Instance, FlowLedger.TestSupport.TestClock.Provider);

        using var cts = new CancellationTokenSource();
        var first = p.GetAccountsAsync("simplefin", cts.Token);
        await cts.CancelAsync();
        var cancelled = async () => await first;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        handler.Gate.SetResult();
        var accounts = await p.GetAccountsAsync("simplefin");
        accounts.Should().HaveCount(2);
        handler.Calls.Should().Be(1, "the one fetch is shared, not repeated or cancelled");
    }

    [Fact]
    public async Task An_account_with_only_pending_rows_advances_its_cursor_instead_of_rereading_the_window()
    {
        var pendingDay = FlowLedger.TestSupport.TestClock.Instant.AddDays(-3);
        var json = $$"""
        { "errors": [], "accounts": [
          { "org": { "id": "arvest", "name": "Arvest Bank" }, "id": "chk1", "name": "Checking", "currency": "USD", "balance": "10",
            "transactions": [ { "id": "p1", "posted": 0, "transacted_at": {{pendingDay.ToUnixTimeSeconds()}}, "pending": true, "amount": "-5.00", "description": "HOLD" } ] } ] }
        """;
        var (p, _) = Make(json);
        var accounts = await p.GetAccountsAsync("simplefin");

        var page = await p.GetTransactionsAsync(accounts[0].ProviderId, SyncCursor.Initial);

        page.Items.Should().HaveCount(1);
        page.NextCursor.IsInitial.Should().BeFalse();
        page.NextCursor.Value.Should().Be(DateOnly.FromDateTime(pendingDay.UtcDateTime).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

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

    [Theory]
    [InlineData("\"currency\": \"EUR\"", true)]
    [InlineData("\"currency\": \"https://example.com/fantasy-coin\"", false)]
    [InlineData("\"currency\": \"\"", false)]
    public async Task An_account_without_an_ISO_currency_is_skipped_instead_of_becoming_USD(string currencyField, bool kept)
    {
        var json = Json(Today).Replace("\"currency\": \"USD\"", currencyField);
        var (p, _) = Make(json);

        var accounts = await p.GetAccountsAsync("simplefin");

        accounts.Should().HaveCount(0 + (kept ? 2 : 0));
        accounts.Should().NotContain(a => a.CurrencyCode == "USD");
    }

    [Fact]
    public async Task Overpaid_card_keeps_its_sign_as_a_negative_amount_owed()
    {
        var json = Json(Today).Replace("\"-350.25\"", "\"200.00\"");
        var (p, _) = Make(json);
        var accounts = await p.GetAccountsAsync("simplefin");

        accounts[1].AccountType.Should().Be("CREDIT_CARD");
        accounts[1].Balance.Amount.Should().Be(-200m); // the bank owes the customer 200
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
        page.Items.Should().OnlyContain(t => t.ProviderCategory == null, "SimpleFIN sends no category, so the categorizer gets no provider hint");
        page.HasMore.Should().BeFalse();
        page.NextCursor.IsInitial.Should().BeFalse();
    }

    [Fact]
    public async Task Cursor_filters_older_transactions_beyond_overlap()
    {
        var old = FlowLedger.TestSupport.TestClock.Instant.AddDays(-40).ToUnixTimeSeconds();
        var (p, _) = Make(Json(old));
        var accounts = await p.GetAccountsAsync("simplefin");
        var cursor = new SyncCursor(FlowLedger.TestSupport.TestClock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

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

    [Theory]
    [InlineData("Chase Bank", "JDH Pro - J. DAVIS (9689)", "-45.60", "CREDIT_CARD")] // card named after the holder: only the sign gives it away
    [InlineData("Chase Bank", "JDH Pro - J. DAVIS (9689)", "250.00", "CHECKING")]
    [InlineData("Arvest Bank", "Everyday Checking", "-20.00", "CHECKING")] // an overdrawn account that says checking stays checking
    [InlineData("Arvest Bank", "Premium Savings", "-5.00", "SAVINGS")]
    [InlineData("Ameriprise Financial", "SPS ADVISOR (9133)", "-1.00", "INVESTMENT")]
    public void Unlabelled_negative_balance_is_a_card_but_named_products_win(string org, string name, string balance, string expected)
    {
        var acct = new SfAccount { Id = "x", Name = name, Balance = balance, Org = new SfOrg { Name = org } };
        SimpleFinMapper.InferType(acct).Should().Be(expected);
    }

    [Fact]
    public void Inferred_card_balance_is_stored_as_a_positive_amount_owed()
    {
        var acct = new SfAccount { Id = "x", Name = "J. DAVIS (9689)", Balance = "-45.60", Currency = "USD", Org = new SfOrg { Name = "Chase Bank" } };
        var mapped = SimpleFinMapper.ToAccount(acct);
        mapped.AccountType.Should().Be("CREDIT_CARD");
        mapped.Balance.Amount.Should().Be(45.60m);
    }
}
