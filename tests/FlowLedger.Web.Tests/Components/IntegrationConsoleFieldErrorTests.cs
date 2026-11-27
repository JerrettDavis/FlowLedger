using System.Net;
using System.Text;
using Bunit;
using FlowLedger.Web.ApiClient;
using FlowLedger.Web.Components.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace FlowLedger.Web.Tests.Components;

/// <summary>
/// #211: the REST and SOAP consoles on the Integrations page mark the request field the API rejected (a 422 validation problem
/// keyed by the request field) and keep what the user typed, instead of showing only a generic failure line.
/// </summary>
public sealed class IntegrationConsoleFieldErrorTests : BunitContext, IAsyncLifetime
{
    private const string State = """
        {"integrations":[
          {"id":"11111111-1111-1111-1111-111111111111","name":"Orders API","kind":"Rest","enabled":true,
           "config":{"url":"https://api.example.com/v1","auth":"None","authUser":null,"headers":{},"events":[],"soapEndpoint":null},
           "hasCredential":false,"hasSigningSecret":false,"createdUtc":"2026-01-01T00:00:00Z","lastUsedUtc":null,"lastError":null},
          {"id":"22222222-2222-2222-2222-222222222222","name":"Calc SOAP","kind":"Soap","enabled":true,
           "config":{"url":"https://calc.example.com/?wsdl","auth":"None","authUser":null,"headers":{},"events":[],"soapEndpoint":null},
           "hasCredential":false,"hasSigningSecret":false,"createdUtc":"2026-01-01T00:00:00Z","lastUsedUtc":null,"lastError":null}],
         "canStoreSecrets":true,"events":[],"limit":20}
        """;

    private const string Operations = """
        {"operations":[{"name":"Add","soapVersion":"1.1","soapAction":"urn:add","endpoint":"https://calc.example.com/","bodySkeleton":"<Add/>"}],"error":null}
        """;

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/rest/call", StringComparison.Ordinal))
            {
                return Task.FromResult(Problem("""{"path":["Use a path under the integration's base URL (like /v1/items), not a full URL."]}"""));
            }

            if (path.EndsWith("/soap/invoke", StringComparison.Ordinal))
            {
                return Task.FromResult(Problem("""{"bodyXml":["Provide the request body XML (256 KB maximum)."]}"""));
            }

            var json = path.EndsWith("/soap/operations", StringComparison.Ordinal) ? Operations : State;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private static HttpResponseMessage Problem(string errors) => new(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("{\"title\":\"Request not sent\",\"errors\":" + errors + "}", Encoding.UTF8, "application/problem+json"),
        };
    }

    private IRenderedComponent<IntegrationsPage> Open(string cardLabel)
    {
        Services.AddMudServices();
        Services.AddSingleton(new FlowLedgerApiClient(
            new HttpClient(new Handler()) { BaseAddress = new Uri("http://localhost") }, NullLogger<FlowLedgerApiClient>.Instance));
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddAuthorization();
        var cut = Render<IntegrationsPage>();
        cut.WaitForAssertion(() => cut.Find($"[aria-label='{cardLabel}']"));
        cut.Find($"[aria-label='{cardLabel}']").Click();
        return cut;
    }

    [Fact]
    public async Task Rest_console_marks_the_path_field_the_api_rejected_and_keeps_what_was_typed()
    {
        var cut = Open("Open Orders API");
        cut.WaitForAssertion(() => cut.FindAll("input").Should().Contain(i => i.GetAttribute("placeholder") == "/items"));
        var path = cut.FindAll("input").Single(i => i.GetAttribute("placeholder") == "/items");
        path.Input("https://evil.example/x");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Send").ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Fix the highlighted fields").And.Contain("not a full URL");
            var field = cut.FindAll("input").Single(i => i.GetAttribute("placeholder") == "/items");
            field.GetAttribute("aria-invalid").Should().Be("true");
            field.GetAttribute("value").Should().Be("https://evil.example/x");
        });
    }

    [Fact]
    public async Task Rest_console_error_clears_when_the_path_is_edited()
    {
        var cut = Open("Open Orders API");
        cut.WaitForAssertion(() => cut.FindAll("input").Should().Contain(i => i.GetAttribute("placeholder") == "/items"));
        cut.FindAll("input").Single(i => i.GetAttribute("placeholder") == "/items").Input("//x");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Send").ClickAsync(new());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("not a full URL"));

        cut.FindAll("input").Single(i => i.GetAttribute("placeholder") == "/items").Input("/items");

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("not a full URL"));
    }

    [Fact]
    public async Task Soap_console_marks_the_request_body_the_api_rejected()
    {
        var cut = Open("Open Calc SOAP");
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Load operations from WSDL", StringComparison.Ordinal)).ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Contains("Call operation", StringComparison.Ordinal)));

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Call operation", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Fix the highlighted fields").And.Contain("Provide the request body XML");
            cut.Find("textarea[aria-invalid='true']");
        });
    }

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
}
