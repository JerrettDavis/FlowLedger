using System.Net;
using System.Text;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Assets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Assets;

public sealed class HttpJsonAppraisalFeedTests
{
    private static Asset Vehicle()
    {
        var a = Asset.Create(TenantId.New(), "Truck", AssetCategory.Vehicle, new Money(20000m, "USD"), new DateOnly(2024, 1, 1));
        a.UpdateDetails("Truck", AssetCategory.Vehicle, "Ford", "F-150 XL", "SERIAL-DO-NOT-SEND", null,
            new DateOnly(2021, 5, 1), null, new Dictionary<string, string> { ["VIN"] = "1FT123" });
        return a;
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpJsonAppraisalFeed Feed(StubHandler h, Action<HttpJsonAppraisalFeedOptions>? tweak = null)
    {
        var o = new HttpJsonAppraisalFeedOptions
        {
            Enabled = true,
            UrlTemplate = "https://prices.example.test/v1/{category}?make={manufacturer}&model={model}&year={year}&vin={attr:VIN}",
            ApiKeyHeader = "X-Api-Key",
            ApiKey = "k",
            ValuePath = "data.items.0.price",
        };
        tweak?.Invoke(o);
        return new HttpJsonAppraisalFeed(new Factory(h), Options.Create(o), NullLogger<HttpJsonAppraisalFeed>.Instance);
    }

    [Fact]
    public void Ships_disabled_by_default()
        => new HttpJsonAppraisalFeed(new Factory(new StubHandler(HttpStatusCode.OK, "{}")),
            Options.Create(new HttpJsonAppraisalFeedOptions()), NullLogger<HttpJsonAppraisalFeed>.Instance)
            .IsEnabled.Should().BeFalse();

    [Fact]
    public async Task Reads_value_via_path_escapes_params_sends_key_header_and_never_sends_serial()
    {
        var h = new StubHandler(HttpStatusCode.OK, """{"data":{"items":[{"price":18250.5}]}}""");

        var quote = await Feed(h).GetQuoteAsync(Vehicle(), new DateOnly(2026, 11, 13));

        quote.Should().NotBeNull();
        quote!.Amount.Should().Be(18250.5m);
        var url = h.Seen!.RequestUri!.AbsoluteUri;
        url.Should().Contain("make=Ford").And.Contain("model=F-150%20XL").And.Contain("year=2021").And.Contain("vin=1FT123");
        url.Should().NotContain("SERIAL");
        h.Seen.Headers.GetValues("X-Api-Key").Should().ContainSingle("k");
    }

    [Fact]
    public async Task Returns_null_on_http_error_bad_path_or_non_https_url()
    {
        var asset = Vehicle();
        var today = new DateOnly(2026, 11, 13);

        (await Feed(new StubHandler(HttpStatusCode.InternalServerError, "{}")).GetQuoteAsync(asset, today)).Should().BeNull();
        (await Feed(new StubHandler(HttpStatusCode.OK, """{"other":1}""")).GetQuoteAsync(asset, today)).Should().BeNull();
        (await Feed(new StubHandler(HttpStatusCode.OK, """{"data":{"items":[{"price":1}]}}"""),
            o => o.UrlTemplate = "http://prices.example.test/x").GetQuoteAsync(asset, today)).Should().BeNull();
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw ex;
    }

    [Fact]
    public async Task Network_failure_and_non_json_bodies_mean_no_value_not_an_exception()
    {
        var today = new DateOnly(2026, 11, 13);
        var o = Options.Create(new HttpJsonAppraisalFeedOptions { Enabled = true, UrlTemplate = "https://prices.example.test/x", ValuePath = "v" });

        var down = new HttpJsonAppraisalFeed(new Factory(new ThrowingHandler(new HttpRequestException("boom"))), o, NullLogger<HttpJsonAppraisalFeed>.Instance);
        var timeout = new HttpJsonAppraisalFeed(new Factory(new ThrowingHandler(new TaskCanceledException("timeout"))), o, NullLogger<HttpJsonAppraisalFeed>.Instance);

        (await down.GetQuoteAsync(Vehicle(), today)).Should().BeNull();
        (await timeout.GetQuoteAsync(Vehicle(), today)).Should().BeNull();
        (await Feed(new StubHandler(HttpStatusCode.OK, "<html>not json</html>")).GetQuoteAsync(Vehicle(), today)).Should().BeNull();
    }

    [Fact]
    public async Task Accepts_numeric_strings_and_rejects_negative_values()
    {
        var today = new DateOnly(2026, 11, 13);
        (await Feed(new StubHandler(HttpStatusCode.OK, """{"data":{"items":[{"price":"1,200.50"}]}}""")).GetQuoteAsync(Vehicle(), today))!
            .Amount.Should().Be(1200.50m);
        (await Feed(new StubHandler(HttpStatusCode.OK, """{"data":{"items":[{"price":-5}]}}""")).GetQuoteAsync(Vehicle(), today))
            .Should().BeNull();
    }
}
