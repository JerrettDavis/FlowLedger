using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Contracts;
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

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
