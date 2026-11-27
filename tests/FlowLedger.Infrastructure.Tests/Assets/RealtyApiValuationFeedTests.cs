using System.Net;
using System.Text;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Assets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Assets;

public sealed class RealtyApiValuationFeedTests
{
    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static RealtyApiValuationFeed Feed(Stub s, bool enabled = true)
        => new(new Factory(s), Options.Create(new RealtyApiOptions { Enabled = enabled, ApiKey = "secret-key" }),
            NullLogger<RealtyApiValuationFeed>.Instance);

    private static Asset Home(string? address)
    {
        var a = Asset.Create(TenantId.New(), "Home", AssetCategory.RealEstate, new Money(400000m, "USD"), new DateOnly(2020, 1, 1), FlowLedger.TestSupport.TestClock.Provider);
        var attrs = address is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Address"] = address };
        a.UpdateDetails("Home", AssetCategory.RealEstate, null, null, "SERIAL-DO-NOT-SEND", null, null, null, attrs);
        return a;
    }

    private const string Body = """{"details":{"avm":{"__root":{"avmInfo":{"predictedValue":518294.54}}}}}""";

    [Fact]
    public void Disabled_without_flag_or_key()
    {
        Feed(new Stub(HttpStatusCode.OK, "{}"), enabled: false).IsEnabled.Should().BeFalse();
        new RealtyApiValuationFeed(new Factory(new Stub(HttpStatusCode.OK, "{}")),
            Options.Create(new RealtyApiOptions { Enabled = true }), NullLogger<RealtyApiValuationFeed>.Instance)
            .IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Reads_predicted_value_sends_key_header_and_only_the_address()
    {
        var stub = new Stub(HttpStatusCode.OK, Body);
        var quote = await Feed(stub).GetQuoteAsync(Home("1 Main St, Austin, TX 78701"), new DateOnly(2026, 11, 16));

        quote.Should().NotBeNull();
        quote!.Amount.Should().Be(518295m);
        stub.Seen!.Headers.GetValues("x-realtyapi-key").Should().ContainSingle("secret-key");
        stub.Seen.RequestUri!.AbsoluteUri.Should().Contain("property_address=1%20Main%20St").And.NotContain("SERIAL");
    }

    [Fact]
    public async Task Returns_null_without_address_on_http_error_or_missing_value()
    {
        (await Feed(new Stub(HttpStatusCode.OK, Body)).GetQuoteAsync(Home(null), new DateOnly(2026, 11, 16))).Should().BeNull();
        (await Feed(new Stub(HttpStatusCode.TooManyRequests, "{}")).GetQuoteAsync(Home("1 Main St"), new DateOnly(2026, 11, 16))).Should().BeNull();
        (await Feed(new Stub(HttpStatusCode.OK, "{}")).GetQuoteAsync(Home("1 Main St"), new DateOnly(2026, 11, 16))).Should().BeNull();
    }
}
