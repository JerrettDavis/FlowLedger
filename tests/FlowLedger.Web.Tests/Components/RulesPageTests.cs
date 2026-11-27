using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>The rule form offers the existing categories instead of free text, so a typo does not create a near-duplicate (#211).</summary>
public sealed class RulesPageTests : BunitContext, IAsyncLifetime
{
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/api/categories" => """[{"id":"11111111-1111-1111-1111-111111111111","path":"Food/Coffee","name":"Coffee","parentId":null}]""",
                _ => "[]",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_category_field_suggests_existing_categories_and_their_parents()
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<RulesPage>();
        cut.WaitForAssertion(() => cut.FindComponents<MudAutocomplete<string>>().Should().HaveCount(1));
        var field = cut.FindComponent<MudAutocomplete<string>>().Instance;

        var search = field.SearchFunc!;
        var all = (await search("", CancellationToken.None)!).ToList();
        var filtered = (await search("cof", CancellationToken.None)!).ToList();

        all.Should().Equal("Food", "Food/Coffee");
        filtered.Should().Equal("Food/Coffee");
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
