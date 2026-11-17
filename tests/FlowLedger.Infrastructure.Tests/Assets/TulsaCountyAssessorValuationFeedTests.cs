using System.Net;
using System.Text;
using FlowLedger.Domain.Aggregates;
using FlowLedger.Domain.ValueObjects;
using FlowLedger.Infrastructure.Assets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowLedger.Infrastructure.Tests.Assets;

public sealed class TulsaCountyAssessorValuationFeedTests
{
    private const string SearchHtml =
        """<script>{"TaxYear":2026,"AccountNo":"R00500920240120","AcctType":"Residential","PropertyZipCode":"74103","FullPropertyStreet":"500 S DENVER AV W"},{"TaxYear":2026,"AccountNo":"R99999999999999","PropertyZipCode":"74104","FullPropertyStreet":"5006 N DENVER PL W"}</script>""";

    private const string InfoHtml =
        """<h5> <i class="fa-solid fa-money-bill-trend-up" id="MarketValueHeader" style="color:green"></i> $215,440 </h5>""";

    private sealed class Stub(Func<string, string> body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(url), Encoding.UTF8, "text/html") });
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static TulsaCountyAssessorValuationFeed Feed(Stub s, bool enabled = true)
        => new(new Factory(s), Options.Create(new TulsaAssessorOptions { Enabled = enabled }),
            NullLogger<TulsaCountyAssessorValuationFeed>.Instance);

    private static Asset Home(Dictionary<string, string> attrs)
    {
        var a = Asset.Create(TenantId.New(), "Home", AssetCategory.RealEstate, new Money(200000m, "USD"), new DateOnly(2020, 1, 1));
        a.UpdateDetails("Home", AssetCategory.RealEstate, null, null, "SERIAL-DO-NOT-SEND", null, null, null, attrs);
        return a;
    }

    private static Stub Site() => new(u => u.Contains("/Property/Info") ? InfoHtml : SearchHtml);

    [Fact]
    public async Task Matches_ordinal_street_and_nine_digit_zip_like_the_real_site()
    {
        var stub = new Stub(u => u.Contains("/Property/Info") ? InfoHtml
            : """{"TaxYear":2026,"AccountNo":"R33810831132270","PropertyZipCode":"741332758","FullPropertyStreet":"7024 E 71 CT S"}""");
        var quote = await Feed(stub).GetQuoteAsync(Home(new() { ["Address"] = "7024 E 71st Ct, Tulsa, OK 74133" }), new DateOnly(2026, 11, 16));
        quote!.Amount.Should().Be(215440m);
        stub.Urls[1].Should().EndWith("accountNo=R33810831132270");
    }

    [Fact]
    public void Parses_address_into_assessor_fields()
    {
        var p = TulsaCountyAssessorValuationFeed.ParseAddress("500 S Denver Ave, Tulsa, OK 74103");
        p.Should().NotBeNull();
        (p!.Value.No, p.Value.PreDir, p.Value.Name, p.Value.Zip).Should().Be(("500", "S", "Denver", "74103"));
        TulsaCountyAssessorValuationFeed.ParseAddress("Not an address").Should().BeNull();
    }

    [Fact]
    public async Task Searches_by_address_picks_zip_match_and_reads_market_value()
    {
        var stub = Site();
        var quote = await Feed(stub).GetQuoteAsync(Home(new() { ["Address"] = "500 S Denver Ave, Tulsa, OK 74103" }), new DateOnly(2026, 11, 16));

        quote.Should().NotBeNull();
        quote!.Amount.Should().Be(215440m);
        stub.Urls.Should().HaveCount(2);
        stub.Urls[1].Should().EndWith("accountNo=R00500920240120");
        stub.Urls.Should().OnlyContain(u => !u.Contains("SERIAL"));
    }

    [Fact]
    public async Task Account_attribute_skips_search()
    {
        var stub = Site();
        var quote = await Feed(stub).GetQuoteAsync(Home(new() { ["AssessorAccountNo"] = "R123" }), new DateOnly(2026, 11, 16));

        quote!.Amount.Should().Be(215440m);
        stub.Urls.Should().ContainSingle().Which.Should().Contain("accountNo=R123");
    }

    [Fact]
    public async Task Ambiguous_or_missing_results_return_null()
    {
        var none = new Stub(_ => "<html></html>");
        (await Feed(none).GetQuoteAsync(Home(new() { ["Address"] = "1 Main St, Tulsa, OK 74120" }), new DateOnly(2026, 11, 16))).Should().BeNull();
        (await Feed(Site()).GetQuoteAsync(Home([]), new DateOnly(2026, 11, 16))).Should().BeNull();
        Feed(Site(), enabled: false).IsEnabled.Should().BeFalse();
    }
}
