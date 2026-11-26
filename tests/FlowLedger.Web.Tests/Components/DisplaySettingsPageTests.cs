using Bunit;
using FlowLedger.Web.Components.Pages;
using FlowLedger.Web.Services;
using FlowLedger.Web.Shared;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

public sealed class DisplaySettingsPageTests : BunitContext, IAsyncLifetime
{
    [Fact]
    public void Defaults_to_usd_en_us_and_previews_dollars()
    {
        Services.AddMudServices();
        Services.AddScoped<DisplaySettingsService>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<DisplaySettingsPage>();

        cut.Find("[data-testid=display-preview]").TextContent.Should().Contain("$1,234,567.89");
    }

    [Fact]
    public void Save_persists_the_cookie_and_updates_the_service()
    {
        Services.AddMudServices();
        Services.AddScoped<DisplaySettingsService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Services.GetRequiredService<DisplaySettingsService>();
        service.Set(new DisplaySettings("EUR", "de-DE"));

        var cut = Render<DisplaySettingsPage>();
        cut.Find("[data-testid=display-save]").Click();

        JSInterop.Invocations.Should().Contain(i => i.Identifier == "FlowLedgerDisplay.save"
            && (string?)i.Arguments[0] == "EUR|de-DE");
        service.Current.Should().Be(new DisplaySettings("EUR", "de-DE"));
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
