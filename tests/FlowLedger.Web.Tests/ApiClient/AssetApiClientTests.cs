using System.Net;
using System.Text;
using FlowLedger.Web.ApiClient;
using FluentAssertions;

namespace FlowLedger.Web.Tests.ApiClient;

public sealed class AssetApiClientTests
{
    private static FlowLedgerApiClient Client(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var http = new HttpClient(new StubHttpMessageHandler(response)) { BaseAddress = new Uri("http://localhost") };
        return new FlowLedgerApiClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowLedgerApiClient>.Instance);
    }

    private static CreateAssetRequest Req() =>
        new("Laptop", "Electronics", 1000m, "USD", null, null, null, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public async Task CreateAssetAsync_surfaces_the_api_problem_detail_on_400()
    {
        var client = Client(HttpStatusCode.BadRequest, """{"title":"Bad Request","status":400,"detail":"The linked account was not found."}""");

        var act = () => client.CreateAssetAsync(Req());

        (await act.Should().ThrowAsync<ApiClientException>()).WithMessage("The linked account was not found.");
    }

    [Fact]
    public async Task CreateAssetAsync_joins_validation_errors()
    {
        var client = Client(HttpStatusCode.BadRequest, """{"errors":{"Category":["Category must be one of: A, B."]}}""");

        var act = () => client.CreateAssetAsync(Req());

        (await act.Should().ThrowAsync<ApiClientException>()).WithMessage("*Category must be one of*");
    }

    [Fact]
    public async Task GetAssetSummaryAsync_deserializes_the_api_shape()
    {
        const string json = """
            {"assetCount":2,"totalValue":1500.5,"totalCost":2000,"unrealizedGainLoss":-499.5,"linkedLiabilities":0,"totalEquity":1500.5,
             "byCategory":[{"category":"Electronics","count":2,"value":1500.5}],"topAssets":[],
             "netWorth":{"accountAssets":10,"accountLiabilities":5,"physicalAssets":1500.5,"netWorth":1505.5,"carriedByAccounts":0}}
            """;

        var summary = await Client(HttpStatusCode.OK, json).GetAssetSummaryAsync();

        summary!.AssetCount.Should().Be(2);
        summary.NetWorth.NetWorth.Should().Be(1505.5m);
        summary.ByCategory.Should().ContainSingle().Which.Category.Should().Be("Electronics");
    }

    [Fact]
    public async Task GetAssetAsync_returns_null_on_404()
        => (await Client(HttpStatusCode.NotFound, "{}").GetAssetAsync(Guid.NewGuid())).Should().BeNull();
}
