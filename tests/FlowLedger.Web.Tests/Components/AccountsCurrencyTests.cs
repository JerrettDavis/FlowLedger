using System.Globalization;
using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Pages;
using FlowLedger.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>A non-USD account is shown in its own currency, never with a dollar sign (#212).</summary>
public sealed class AccountsCurrencyTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler(string accountsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.RequestUri!.AbsolutePath == "/api/accounts" ? accountsJson : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static string Account(string name, decimal balance, string currency) =>
        $$"""{"id":"{{Guid.NewGuid()}}","name":"{{name}}","accountType":"Checking","balanceAmount":{{balance.ToString(CultureInfo.InvariantCulture)}},"balanceCurrency":"{{currency}}","isActive":true}""";

    private IRenderedComponent<Accounts> RenderAccounts(params string[] accounts)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler("[" + string.Join(",", accounts) + "]")) { BaseAddress = new Uri("http://localhost") },
            NullLogger<FlowLedgerApiClient>.Instance));
        Services.AddScoped<ThemeService>();
        Services.AddScoped<DisplaySettingsService>();
        Services.AddSingleton(new FlowLedger.Web.Auth.OidcOptions());
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        Services.AddSingleton(_ => new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance));
        Services.AddSingleton(sp => sp.GetRequiredService<ComponentStatePersistenceManager>().State);
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
        return Render<Accounts>();
    }

    [Fact]
    public void A_euro_account_and_its_group_total_show_the_euro_sign_and_no_dollar()
    {
        var cut = RenderAccounts(Account("Sparkasse", 1234.5m, "EUR"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sparkasse"));
        cut.Markup.Should().Contain("€1,234.50");
        cut.Markup.Should().NotContain("$");
    }

    [Fact]
    public void A_group_holding_two_currencies_does_not_add_them_together()
    {
        var cut = RenderAccounts(Account("Checking USD", 100m, "USD"), Account("Checking EUR", 50m, "EUR"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mixed currencies"));
        cut.Markup.Should().NotContain("$150").And.NotContain("€150");
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
