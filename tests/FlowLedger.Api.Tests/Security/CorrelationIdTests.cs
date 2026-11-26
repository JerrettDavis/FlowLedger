using FluentAssertions;

namespace FlowLedger.Api.Tests.Security;

[Collection("ApiIntegration")]
public sealed class CorrelationIdTests(FlowLedgerApiFactory factory)
{
    private const string Header = "X-Correlation-Id";

    [Fact]
    public async Task A_supplied_correlation_id_is_echoed_back()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add(Header, "web-req-12345");

        var response = await client.SendAsync(request);

        response.Headers.GetValues(Header).Should().ContainSingle().Which.Should().Be("web-req-12345");
    }

    [Fact]
    public async Task A_missing_correlation_id_is_generated_even_on_error_responses()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/accounts"); // 401: unauthenticated

        response.Headers.GetValues(Header).Should().ContainSingle().Which.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("has spaces and \"quotes\"")]
    [InlineData("<script>alert(1)</script>")]
    public async Task An_unsafe_correlation_id_is_replaced_not_reflected(string bad)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.TryAddWithoutValidation(Header, bad);

        var response = await client.SendAsync(request);

        var id = response.Headers.GetValues(Header).Single();
        id.Should().NotBe(bad);
        id.Should().MatchRegex("^[A-Za-z0-9._-]{1,64}$");
    }

    [Fact]
    public async Task An_overlong_correlation_id_is_replaced()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/alive");
        request.Headers.Add(Header, new string('a', 200));

        var response = await client.SendAsync(request);

        response.Headers.GetValues(Header).Single().Length.Should().BeLessThanOrEqualTo(64);
    }
}
