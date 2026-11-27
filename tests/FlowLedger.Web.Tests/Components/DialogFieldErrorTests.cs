using System.Net;
using System.Text;
using AngleSharp.Dom;
using Bunit;
using FlowLedger.TestSupport.Builders;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>
/// The asset and recurring-flow dialogs show the API's per-field validation errors next to the fields and stay open (#211),
/// like the account dialog does.
/// </summary>
public sealed class DialogFieldErrorTests : BunitContext, IAsyncLifetime
{
    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null", Encoding.UTF8, "application/json") });
    }

    private static readonly AccountDto Checking = new(
        Guid.NewGuid(), "Checking", "Checking", 10m, "USD", null, null, true, DateTimeOffset.UnixEpoch, null);

    private IRenderedComponent<MudDialogProvider> Provider()
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new EmptyHandler()) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<MudDialogProvider>();
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenAssetDialogAsync(Func<object, Task> submit)
    {
        var provider = Provider();
        _ = Services.GetRequiredService<IDialogService>().ShowAsync<AssetFormDialog>("Add Asset", new DialogParameters<AssetFormDialog>
        {
            { x => x.Asset, null },
            { x => x.Accounts, [Checking] },
            { x => x.SubmitAsync, submit },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Asset form"));
        return provider;
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenFlowDialogAsync(Func<CreateRecurringFlowRequest, Task> create)
    {
        var provider = Provider();
        _ = Services.GetRequiredService<IDialogService>().ShowAsync<RecurringFlowFormDialog>("Create Recurring Flow", new DialogParameters<RecurringFlowFormDialog>
        {
            { x => x.Flow, null },
            { x => x.Accounts, [Checking] },
            { x => x.CreateAsync, create },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Recurring flow form"));
        return provider;
    }

    private static ApiClientException Rejected(string field, string message) =>
        new("Validation failed", HttpStatusCode.BadRequest, fieldErrors: new Dictionary<string, string> { [field] = message });

    [Fact]
    public async Task Asset_api_field_errors_appear_on_the_field_and_the_dialog_stays_open()
    {
        var provider = await OpenAssetDialogAsync(_ => throw Rejected("name", "An asset with this name already exists."));
        provider.Find("input[aria-label='Asset name']").Change("Boat");

        await provider.Find("button[aria-label='Add asset']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("An asset with this name already exists.").And.Contain("Fix the highlighted fields");
            provider.Find("input[aria-label='Asset name']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Asset_field_error_clears_when_the_field_is_edited_and_a_plain_failure_is_an_alert()
    {
        var provider = await OpenAssetDialogAsync(_ => throw Rejected("name", "Taken."));
        provider.Find("input[aria-label='Asset name']").Change("Boat");
        await provider.Find("button[aria-label='Add asset']").ClickAsync();
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Taken."));

        provider.Find("input[aria-label='Asset name']").Change("Boat 2");

        provider.WaitForAssertion(() => provider.Markup.Should().NotContain("Taken."));
    }

    [Fact]
    public async Task Asset_without_field_errors_shows_the_message_inside_the_dialog()
    {
        var provider = await OpenAssetDialogAsync(_ => throw new ApiClientException("The server is unavailable."));
        provider.Find("input[aria-label='Asset name']").Change("Boat");

        await provider.Find("button[aria-label='Add asset']").ClickAsync();

        provider.WaitForAssertion(() => provider.Find("[role=alert]").TextContent.Should().Contain("The server is unavailable."));
    }

    [Fact]
    public async Task Recurring_flow_api_field_errors_appear_on_the_field_and_the_dialog_stays_open()
    {
        var provider = await OpenFlowDialogAsync(_ => throw Rejected("name", "A flow with this name already exists."));
        provider.Find("input[aria-label='Flow name']").Change("Rent");
        provider.Find("input[aria-label='Amount']").Change("1200");

        await provider.Find("button[aria-label='Create flow']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("A flow with this name already exists.");
            provider.Find("input[aria-label='Flow name']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Recurring_flow_amount_error_from_the_api_is_shown_on_the_amount_field()
    {
        var provider = await OpenFlowDialogAsync(_ => throw Rejected("amount", "Amount must be positive."));
        provider.Find("input[aria-label='Flow name']").Change("Rent");
        provider.Find("input[aria-label='Amount']").Change("5");

        await provider.Find("button[aria-label='Create flow']").ClickAsync();

        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Amount must be positive."));
    }

    // ── SaveReportDialog, SheetBillDialog, IntegrationFormDialog and the recurring-flow edit path (#211) ──

    private async Task<IRenderedComponent<MudDialogProvider>> OpenSaveReportDialogAsync(Func<SaveReportDialog.Outcome, Task> commit)
    {
        var provider = Provider();
        _ = Services.GetRequiredService<IDialogService>().ShowAsync<SaveReportDialog>("Save view", new DialogParameters<SaveReportDialog>
        {
            { x => x.AsPanel, false },
            { x => x.Reports, [] },
            { x => x.SuggestedName, "Spending" },
            { x => x.CommitAsync, commit },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Save view"));
        return provider;
    }

    private static IElement SaveButton(IRenderedComponent<MudDialogProvider> provider) =>
        provider.FindAll("button").Single(b => b.TextContent.Trim() == "Save");

    [Fact]
    public async Task Save_report_api_field_error_appears_on_the_name_field_and_the_dialog_stays_open()
    {
        var provider = await OpenSaveReportDialogAsync(_ => throw Rejected("name", "A report with this name already exists."));

        await SaveButton(provider).ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("A report with this name already exists.").And.Contain("Fix the highlighted fields");
            provider.Find("input[aria-label='Name']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Save_report_error_clears_when_the_name_is_edited_and_a_successful_commit_closes_the_dialog()
    {
        var attempts = 0;
        var provider = await OpenSaveReportDialogAsync(_ => ++attempts == 1 ? throw Rejected("name", "Taken.") : Task.CompletedTask);
        await SaveButton(provider).ClickAsync();
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Taken."));

        provider.Find("input[aria-label='Name']").Input("Spending 2");
        provider.WaitForAssertion(() => provider.Markup.Should().NotContain("Taken."));
        await SaveButton(provider).ClickAsync();

        provider.WaitForAssertion(() => provider.Markup.Should().NotContain("Save view"));
        attempts.Should().Be(2);
    }

    private async Task<IRenderedComponent<MudDialogProvider>> OpenSheetBillDialogAsync(Func<SaveBillRequest, Task> save)
    {
        var provider = Provider();
        _ = Services.GetRequiredService<IDialogService>().ShowAsync<SheetBillDialog>("Add row", new DialogParameters<SheetBillDialog>
        {
            { x => x.Row, null },
            { x => x.Sections, ["Bills"] },
            { x => x.SaveAsync, save },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Sheet row form"));
        return provider;
    }

    [Fact]
    public async Task Sheet_bill_api_field_errors_appear_on_the_field_and_the_dialog_stays_open()
    {
        var provider = await OpenSheetBillDialogAsync(_ => throw Rejected("estimated", "Amounts cannot be negative."));
        provider.Find("input[aria-label='Row name']").Change("Power");

        await SaveButton(provider).ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Amounts cannot be negative.").And.Contain("Fix the highlighted fields");
            provider.Find("input[aria-label='Estimated amount']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Sheet_bill_failure_without_field_errors_is_an_alert_inside_the_dialog()
    {
        var provider = await OpenSheetBillDialogAsync(_ => throw new ApiClientException("The server is unavailable."));
        provider.Find("input[aria-label='Row name']").Change("Power");

        await SaveButton(provider).ClickAsync();

        provider.WaitForAssertion(() => provider.Find("[role=alert]").TextContent.Should().Contain("The server is unavailable."));
    }

    private sealed class RejectingHandler(string fieldsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"title\":\"One or more validation errors occurred.\",\"errors\":" + fieldsJson + "}", Encoding.UTF8, "application/problem+json"),
            });
    }

    [Fact]
    public async Task Integration_api_field_errors_from_a_real_400_appear_on_the_url_field_and_the_dialog_stays_open()
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new RejectingHandler("""{"url":["Use an https address."]}""")) { BaseAddress = new Uri("http://localhost") },
            NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        var provider = Render<MudDialogProvider>();
        _ = Services.GetRequiredService<IDialogService>().ShowAsync<IntegrationFormDialog>("Add integration", new DialogParameters<IntegrationFormDialog>
        {
            { x => x.Existing, null },
            { x => x.CanStoreSecrets, true },
            { x => x.Events, [] },
            { x => x.InitialKind, "Rest" },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Integration name"));
        provider.Find("input[aria-label='Integration name']").Input("Hook");
        provider.Find("input[aria-label='Base URL']").Input("http://example.com");

        await provider.FindAll("button").Single(b => b.TextContent.Trim() == "Add").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Use an https address.");
            provider.Find("input[aria-label='Base URL']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Recurring_flow_edit_stays_open_and_shows_the_api_error_on_the_amount_field()
    {
        var provider = Provider();
        var flow = RecurringFlowDtoBuilder.A().OnAccount(Checking.Id).WithAmount(50m).Build();
        _ = Services.GetRequiredService<IDialogService>().ShowAsync<RecurringFlowFormDialog>("Edit Recurring Flow", new DialogParameters<RecurringFlowFormDialog>
        {
            { x => x.Flow, flow },
            { x => x.Accounts, [Checking] },
            { x => x.UpdateAsync, (Func<UpdateRecurringFlowRequest, Task>)(_ => throw Rejected("amount", "Amount must be positive.")) },
        });
        await provider.WaitForAssertionAsync(() => provider.Markup.Should().Contain("Recurring flow form"));

        await provider.Find("button[aria-label='Save changes']").ClickAsync();

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Amount must be positive.");
            provider.Find("input[aria-label='Amount']").GetAttribute("aria-invalid").Should().Be("true");
        });
    }

    [Fact]
    public async Task Money_inputs_carry_the_display_currency_symbol_as_an_adornment()
    {
        var provider = await OpenFlowDialogAsync(_ => Task.CompletedTask);
        provider.Find(".mud-input-adornment-start").TextContent.Should().Contain("$");

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new FlowLedger.Web.Shared.DisplaySettings("EUR", "de-DE").ToCulture();
            FlowLedger.Web.Shared.Format.CurrencyAdornment.Should().Be("€");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
