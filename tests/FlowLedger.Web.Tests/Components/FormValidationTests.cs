using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Dialogs;
using FlowLedger.Web.Components.Pages;
using FlowLedger.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

public sealed class FormValidationTests : BunitContext, IAsyncLifetime
{
    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") });
    }

    private void Arrange()
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new EmptyHandler()) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAccountDialogAsync(Func<FlowLedger.Contracts.CreateAccountRequest, Task> create)
    {
        Arrange();
        var provider = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        _ = dialogs.ShowAsync<AccountFormDialog>("Create Account", new DialogParameters<AccountFormDialog>
        {
            { x => x.Account, null },
            { x => x.CreateAsync, create },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Account form"));
        return provider;
    }

    private static void Fill(IRenderedComponent<MudDialogProvider> provider, string label, string value) =>
        provider.Find($"input[aria-label='{label}']").Change(value);

    [Fact]
    public async Task Api_field_errors_appear_on_the_field_that_caused_them_and_the_dialog_stays_open()
    {
        var provider = await OpenAccountDialogAsync(_ => throw new ApiClientException(
            "Validation failed", HttpStatusCode.BadRequest,
            fieldErrors: new Dictionary<string, string> { ["name"] = "An account with this name already exists." }));
        Fill(provider, "Account name", "Checking");

        await provider.Find("button[aria-label='Create account']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("An account with this name already exists.");
            provider.Find("input[aria-label='Account name']").GetAttribute("aria-invalid").Should().Be("true");
            provider.Markup.Should().Contain("Fix the highlighted fields");
        });
    }

    [Fact]
    public async Task Editing_a_field_clears_its_server_error()
    {
        var provider = await OpenAccountDialogAsync(_ => throw new ApiClientException(
            "Validation failed", HttpStatusCode.BadRequest,
            fieldErrors: new Dictionary<string, string> { ["name"] = "Taken." }));
        Fill(provider, "Account name", "Checking");
        await provider.Find("button[aria-label='Create account']").ClickAsync();
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Taken."));

        Fill(provider, "Account name", "Checking 2");

        provider.WaitForAssertion(() => provider.Markup.Should().NotContain("Taken."));
    }

    [Fact]
    public async Task A_failure_without_field_errors_is_shown_inside_the_dialog_as_an_alert()
    {
        var provider = await OpenAccountDialogAsync(_ => throw new ApiClientException("The server is unavailable."));
        Fill(provider, "Account name", "Checking");

        await provider.Find("button[aria-label='Create account']").ClickAsync();

        provider.WaitForAssertion(() => provider.Find("[role=alert]").TextContent.Should().Contain("The server is unavailable."));
    }

    [Fact]
    public async Task A_bad_currency_code_is_flagged_inline_and_nothing_is_sent()
    {
        var calls = 0;
        var provider = await OpenAccountDialogAsync(_ => { calls++; return Task.CompletedTask; });
        Fill(provider, "Account name", "Checking");
        Fill(provider, "Currency code", "US");

        await provider.Find("button[aria-label='Create account']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Use a 3-letter currency code");
            provider.Find("input[aria-label='Currency code']").GetAttribute("aria-invalid").Should().Be("true");
        });
        calls.Should().Be(0);
    }

    [Fact]
    public async Task A_missing_name_is_flagged_inline_and_nothing_is_sent()
    {
        var calls = 0;
        var provider = await OpenAccountDialogAsync(_ => { calls++; return Task.CompletedTask; });

        await provider.Find("button[aria-label='Create account']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Name is required");
            provider.Find("input[aria-label='Account name']").GetAttribute("aria-invalid").Should().Be("true");
        });
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData("", 100, "Enter a goal name to save.")]
    [InlineData("Trip", 0, "Enter a target amount above zero to save.")]
    [InlineData("Trip", 100, null)]
    public void Goal_rules_explain_why_save_is_unavailable(string name, double target, string? expected) =>
        FormRules.Goal(name, (decimal)target).Should().Be(expected);

    [Fact]
    public void Budget_rule_and_debt_rules_name_what_is_missing()
    {
        FormRules.Budget(null).Should().Contain("category");
        FormRules.Budget("Food").Should().BeNull();
        FormRules.Rule("", "x").Should().Contain("description");
        FormRules.Rule("coffee", "").Should().Contain("category");
        FormRules.Rule("coffee", "Food").Should().BeNull();
        FormRules.Debt(0).Should().Contain("Add at least one debt");
        FormRules.Debt(2).Should().BeNull();
    }

    [Fact]
    public void Goals_page_shows_the_reason_next_to_the_disabled_save_button()
    {
        Arrange();
        this.AddAuthorization();

        var cut = Render<GoalsPage>();
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Add goal") || b.TextContent.Contains("New goal") || b.TextContent.Contains("Save goal")));
        var openForm = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains("Add goal") || b.TextContent.Contains("New goal"));
        openForm?.Click();

        cut.WaitForAssertion(() =>
        {
            var save = cut.Find("button[aria-describedby='goal-reason']");
            save.HasAttribute("disabled").Should().BeTrue();
            cut.Find("#goal-reason").TextContent.Should().Contain("Enter a goal name");
        });
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
