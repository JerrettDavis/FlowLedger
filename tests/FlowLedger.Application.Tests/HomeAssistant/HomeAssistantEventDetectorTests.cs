using FlowLedger.Application.Features.HomeAssistant;
using FluentAssertions;

namespace FlowLedger.Application.Tests.HomeAssistant;

public sealed class HomeAssistantEventDetectorTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);
    private static readonly HomeAssistantSettings Alerts = HomeAssistantSettings.Default with { Enabled = true, HasWebhook = true };

    private static HaFlow Bill(string name, params DateOnly[] dates) => new(Guid.NewGuid(), name, null, false, 1234.56m, dates);
    private static HaFlow Pay(string name, params DateOnly[] dates) => new(Guid.NewGuid(), name, null, true, 2500m, dates);

    private static IReadOnlyList<HaEvent> Run(
        IReadOnlyList<HaFlow>? flows = null, IReadOnlyList<HaAccountBalance>? accounts = null,
        IReadOnlyList<HaRecentTransaction>? recent = null, IReadOnlyList<HaBudgetBreach>? budgets = null,
        decimal? low = null, HomeAssistantSettings? settings = null)
        => HomeAssistantEventDetector.Detect(Today, flows ?? [], accounts ?? [], recent ?? [], budgets ?? [], low, settings ?? Alerts);

    [Fact]
    public void Bill_inside_the_lead_window_is_due_soon_without_any_numbers()
    {
        var e = Run([Bill("Mortgage", Today.AddDays(2))]).Single();

        e.Kind.Should().Be(HaEventKinds.BillDueSoon);
        e.DueInDays.Should().Be(2);
        e.Message.Should().Be("\"Mortgage\" is due in 2 days.");
        e.Amount.Should().BeNull();
        e.Balance.Should().BeNull();
    }

    [Fact]
    public void Bill_outside_the_lead_window_is_quiet()
        => Run([Bill("Mortgage", Today.AddDays(4))]).Should().BeEmpty();

    [Fact]
    public void Amounts_appear_only_when_the_household_opts_in()
    {
        var e = Run([Bill("Mortgage", Today)], settings: Alerts with { ShareAmounts = true }).Single();
        e.Amount.Should().Be(1234.56m);
        HomeAssistantService.ToPayload(e).Should().ContainKey("amount");

        var quiet = Run([Bill("Mortgage", Today)]).Single();
        HomeAssistantService.ToPayload(quiet).Keys.Should().NotContain(["amount", "balance"]);
    }

    [Fact]
    public void Names_can_be_withheld()
    {
        var e = Run([Bill("Mortgage", Today)], settings: Alerts with { IncludeNames = false }).Single();
        e.Subject.Should().BeNull();
        e.Message.Should().NotContain("Mortgage");
    }

    [Fact]
    public void Paid_bill_is_reported_and_no_longer_due_soon()
    {
        var recent = new[] { new HaRecentTransaction("ACH MORTGAGE PMT 8841", null, false, 1234.56m, Today.AddDays(-1)) };
        var events = Run([Bill("Mortgage", Today.AddDays(-1), Today.AddDays(30))], recent: recent);

        events.Should().ContainSingle(e => e.Kind == HaEventKinds.BillPaid);
        events.Should().NotContain(e => e.Kind == HaEventKinds.BillDueSoon);
    }

    [Fact]
    public void Paycheck_coming_and_landed_use_income_kinds()
    {
        Run([Pay("Acme Payroll", Today.AddDays(1))]).Single().Kind.Should().Be(HaEventKinds.PaycheckExpected);

        var recent = new[] { new HaRecentTransaction("ACME PAYROLL DIRECT DEP", null, true, 2500m, Today) };
        Run([Pay("Acme Payroll", Today)], recent: recent).Single().Kind.Should().Be(HaEventKinds.PaycheckReceived);
    }

    [Fact]
    public void A_debit_does_not_count_as_a_paycheck()
    {
        var recent = new[] { new HaRecentTransaction("ACME PAYROLL REVERSAL", null, false, 2500m, Today) };
        Run([Pay("Acme Payroll", Today)], recent: recent).Single().Kind.Should().Be(HaEventKinds.PaycheckExpected);
    }

    [Fact]
    public void Overdrawn_liquid_account_raises_overdraft_and_suppresses_the_forecast_warning()
    {
        var accounts = new[] { new HaAccountBalance(Guid.NewGuid(), "Checking", -42m, true), new HaAccountBalance(Guid.NewGuid(), "Card", -900m, false) };
        var events = Run(accounts: accounts, low: -100m);

        events.Should().ContainSingle().Which.Kind.Should().Be(HaEventKinds.Overdraft);
        events[0].Balance.Should().BeNull();
    }

    [Fact]
    public void Projected_negative_balance_raises_the_forecast_warning_once_nothing_is_overdrawn()
        => Run(low: -10m).Single().Kind.Should().Be(HaEventKinds.LowBalanceForecast);

    [Fact]
    public void Budget_breaches_become_alerts_keyed_per_month()
    {
        var events = Run(budgets: [new HaBudgetBreach("Groceries", true), new HaBudgetBreach("Dining", false)]);
        events.Select(e => e.Severity).Should().BeEquivalentTo(["error", "warning"]);
        events.Select(e => e.DedupeKey).Should().OnlyContain(k => k.EndsWith("2026-11"));
    }

    [Fact]
    public void Disabled_kinds_are_filtered_out()
        => Run([Bill("Mortgage", Today)], settings: Alerts with { Kinds = [HaEventKinds.Overdraft] }).Should().BeEmpty();

    [Fact]
    public void Dedupe_keys_are_stable_across_runs()
    {
        var flow = Bill("Mortgage", Today.AddDays(1));
        Run([flow]).Single().DedupeKey.Should().Be(Run([flow]).Single().DedupeKey);
    }
}

public sealed class HomeAssistantWebhookUrlTests
{
    private const string Id = "AbCdEf0123456789XyZ";

    [Theory]
    [InlineData("https://ha.example.com/api/webhook/" + Id)]
    [InlineData("http://homeassistant.local:8123/api/webhook/" + Id)]
    [InlineData("http://192.168.1.20:8123/api/webhook/" + Id)]
    [InlineData("http://10.0.0.5:8123/api/webhook/" + Id)]
    [InlineData("http://homeassistant:8123/api/webhook/" + Id)]
    public void Accepts_https_and_local_network_http(string url)
        => HomeAssistantWebhookUrl.TryValidate(url, out _, out _).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("ftp://ha.example.com/api/webhook/" + Id)]
    [InlineData("http://ha.example.com/api/webhook/" + Id)]
    [InlineData("https://user:pw@ha.example.com/api/webhook/" + Id)]
    [InlineData("https://ha.example.com/api/webhook/" + Id + "?x=1")]
    [InlineData("https://ha.example.com/api/webhook/short")]
    [InlineData("https://ha.example.com/other/" + Id)]
    [InlineData("https://ha.example.com/api/webhook/" + Id + "/more")]
    [InlineData("http://169.254.169.254/api/webhook/" + Id)]
    [InlineData("https://169.254.169.254/api/webhook/" + Id)]
    public void Rejects_unsafe_or_malformed_urls(string url)
        => HomeAssistantWebhookUrl.TryValidate(url, out _, out var error).Should().BeFalse(error);

    [Fact]
    public void Display_host_never_includes_the_secret_id()
        => HomeAssistantWebhookUrl.DisplayHost("https://ha.example.com:8123/api/webhook/" + Id)
            .Should().Be("ha.example.com:8123");
}
