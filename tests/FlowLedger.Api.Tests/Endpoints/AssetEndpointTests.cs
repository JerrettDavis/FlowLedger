using System.Net;
using System.Net.Http.Json;
using FlowLedger.Application.Features.Accounts;
using FlowLedger.Application.Features.Assets;
using FluentAssertions;

namespace FlowLedger.Api.Tests.Endpoints;

[Collection("ApiIntegration")]
public sealed class AssetEndpointTests(FlowLedgerApiFactory factory) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await factory.ResetAsync();
        _client = factory.CreateAuthenticatedClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static CreateAssetRequest NewAsset(
        string name = "MacBook", string category = "Electronics", decimal value = 2000m, Guid? link = null,
        Dictionary<string, string>? attrs = null, decimal? price = 2400m, string? method = "Manual", decimal? rate = null, DateOnly? valuedOn = null) =>
        new(name, category, value, "USD", valuedOn, new DateOnly(2024, 1, 1), price, "Apple", "MBP 14", "SN-SECRET-1", "note",
            method, rate, null, null, link, attrs);

    private async Task<AssetDto> Create(CreateAssetRequest r)
    {
        var resp = await _client.PostAsJsonAsync("/api/assets", r);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<AssetDto>())!;
    }

    private async Task<AccountDto> CreateAccount(string name, string type, decimal balance, HttpClient? client = null)
    {
        var resp = await (client ?? _client).PostAsJsonAsync("/api/accounts",
            new CreateAccountRequest(name, type, balance, "USD", null, null));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<AccountDto>())!;
    }

    private async Task<AssetSummaryDto> Summary()
        => (await _client.GetFromJsonAsync<AssetSummaryDto>("/api/assets/summary"))!;

    [Fact]
    public async Task Create_get_list_roundtrip_with_fixed_and_custom_fields()
    {
        var created = await Create(NewAsset(attrs: new() { ["Storage"] = "1TB" }));

        created.Manufacturer.Should().Be("Apple");
        created.SerialNumber.Should().Be("SN-SECRET-1");
        created.Attributes["Storage"].Should().Be("1TB");
        created.Valuations.Should().ContainSingle();

        var list = await _client.GetFromJsonAsync<List<AssetDto>>("/api/assets");
        list.Should().ContainSingle(a => a.Id == created.Id);
        list![0].Valuations.Should().BeNull();
    }

    [Fact]
    public async Task Second_valuation_on_existing_asset_persists_and_updates_current_value()
    {
        var a = await Create(NewAsset());

        var resp = await _client.PostAsJsonAsync($"/api/assets/{a.Id}/valuations",
            new RecordValuationRequest(1500m, DateOnly.FromDateTime(DateTime.UtcNow), "Appraiser", "Insurance appraisal"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var third = await _client.PostAsJsonAsync($"/api/assets/{a.Id}/valuations",
            new RecordValuationRequest(1400m, DateOnly.FromDateTime(DateTime.UtcNow), null, null));
        third.StatusCode.Should().Be(HttpStatusCode.OK);

        var reread = await _client.GetFromJsonAsync<AssetDto>($"/api/assets/{a.Id}");
        reread!.CurrentValue.Should().Be(1400m);
        reread.Valuations.Should().HaveCount(3);
        reread.Valuations!.Select(v => v.Source).Should().Contain(["Appraiser", "Manual"]);
    }

    [Fact]
    public async Task Editing_a_custom_field_persists()
    {
        var a = await Create(NewAsset("Rifle", "Firearm", 800m, attrs: new() { ["Caliber"] = ".22" }));

        var upd = new UpdateAssetRequest("Rifle", "Firearm", null, 800m, "Ruger", "10/22", "SN-2", null, "Manual", null, null, null,
            null, new() { ["Caliber"] = ".223", ["Action"] = "Semi" });
        (await _client.PutAsJsonAsync($"/api/assets/{a.Id}", upd)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reread = await _client.GetFromJsonAsync<AssetDto>($"/api/assets/{a.Id}");
        reread!.Attributes.Should().BeEquivalentTo(new Dictionary<string, string> { ["Caliber"] = ".223", ["Action"] = "Semi" });
        reread.Manufacturer.Should().Be("Ruger");
        reread.SerialNumber.Should().Be("SN-2");

        // Removing every field must persist too.
        var clear = upd with { Attributes = new() };
        (await _client.PutAsJsonAsync($"/api/assets/{a.Id}", clear)).EnsureSuccessStatusCode();
        (await _client.GetFromJsonAsync<AssetDto>($"/api/assets/{a.Id}"))!.Attributes.Should().BeEmpty();
    }

    [Fact]
    public async Task Linking_to_another_tenants_account_is_rejected()
    {
        var otherTenant = factory.CreateAuthenticatedClient(Guid.NewGuid());
        var foreign = await CreateAccount("Their Mortgage", "Mortgage", 0m, otherTenant);

        var resp = await _client.PostAsJsonAsync("/api/assets", NewAsset(link: foreign.Id));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetFromJsonAsync<List<AssetDto>>("/api/assets"))!.Should().BeEmpty();
    }

    [Fact]
    public async Task Linking_to_missing_account_is_rejected_and_unknown_asset_is_404()
    {
        (await _client.PostAsJsonAsync("/api/assets", NewAsset(link: Guid.NewGuid()))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await _client.GetAsync($"/api/assets/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.PostAsJsonAsync($"/api/assets/{Guid.NewGuid()}/valuations", new RecordValuationRequest(1m, null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Validation_rejects_bad_category_and_negative_value()
    {
        (await _client.PostAsJsonAsync("/api/assets", NewAsset(category: "Nope"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsJsonAsync("/api/assets", NewAsset(value: -1m))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unlinked_asset_adds_to_net_worth()
    {
        await CreateAccount("Checking", "Checking", 1000m);
        await Create(NewAsset(value: 500m));

        var s = await Summary();
        s.NetWorth.AccountAssets.Should().Be(1000m);
        s.NetWorth.PhysicalAssets.Should().Be(500m);
        s.NetWorth.NetWorth.Should().Be(1500m);
        s.AssetCount.Should().Be(1);
    }

    [Fact]
    public async Task Asset_linked_to_manual_asset_account_is_counted_once_and_valuation_updates_the_account()
    {
        var acct = await CreateAccount("House value", "ManualAsset", 0m);
        var house = await Create(NewAsset("House", "RealEstate", 300000m, link: acct.Id, price: 250000m));

        var s = await Summary();
        s.NetWorth.NetWorth.Should().Be(300000m);
        s.NetWorth.PhysicalAssets.Should().Be(0m);
        s.NetWorth.CarriedByAccounts.Should().Be(300000m);

        (await _client.PostAsJsonAsync($"/api/assets/{house.Id}/valuations",
            new RecordValuationRequest(320000m, null, "Appraiser", null))).EnsureSuccessStatusCode();

        var account = await _client.GetFromJsonAsync<AccountDto>($"/api/accounts/{acct.Id}");
        account!.BalanceAmount.Should().Be(320000m);
        (await Summary()).NetWorth.NetWorth.Should().Be(320000m);
    }

    [Fact]
    public async Task Asset_linked_to_mortgage_shows_equity_without_double_subtracting_the_liability()
    {
        var mortgage = await CreateAccount("Mortgage", "Mortgage", 200000m);
        await Create(NewAsset("House", "RealEstate", 300000m, link: mortgage.Id, price: 250000m));

        var s = await Summary();
        s.NetWorth.AccountLiabilities.Should().Be(200000m);
        s.NetWorth.PhysicalAssets.Should().Be(300000m);
        s.NetWorth.NetWorth.Should().Be(100000m); // 300k asset - 200k liability, liability subtracted once
        s.LinkedLiabilities.Should().Be(200000m);
        s.TotalEquity.Should().Be(100000m);
        s.TopAssets.Single().Equity.Should().Be(100000m);
        s.TopAssets.Single().LinkedAccountName.Should().Be("Mortgage");
    }

    [Fact]
    public async Task Asset_linked_to_deactivated_account_counts_as_unlinked()
    {
        var acct = await CreateAccount("Old house acct", "ManualAsset", 0m);
        await Create(NewAsset("House", "RealEstate", 100000m, link: acct.Id));
        (await _client.DeleteAsync($"/api/accounts/{acct.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var s = await Summary();
        s.NetWorth.PhysicalAssets.Should().Be(100000m);
        s.NetWorth.NetWorth.Should().Be(100000m);
    }

    [Fact]
    public async Task Refresh_with_model_feed_records_estimate_and_unknown_feed_is_400()
    {
        // Created in 2024 with a 20% declining model → the model feed must lower the value.
        var a = await Create(NewAsset(value: 1000m, method: "DecliningBalance", rate: 0.2m,
            valuedOn: DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-1)));

        var resp = await _client.PostAsync($"/api/assets/{a.Id}/refresh", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await resp.Content.ReadFromJsonAsync<AssetRefreshResultDto>())!;
        result.Updated.Should().BeTrue();
        result.Asset!.CurrentValue.Should().BeLessThan(1000m);
        result.Asset.Valuations!.Select(v => v.Source).Should().Contain("model");

        (await _client.PostAsync($"/api/assets/{a.Id}/refresh?feed=nope", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // The HTTP appraisal feed ships disabled.
        (await _client.PostAsync($"/api/assets/{a.Id}/refresh?feed=appraiser", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Categories_endpoint_lists_templates_and_delete_removes_asset()
    {
        var cats = await _client.GetFromJsonAsync<List<AssetCategoryTemplateDto>>("/api/assets/categories");
        cats!.Single(c => c.Category == "Firearm").SuggestedFields.Should().Contain("Caliber");

        var a = await Create(NewAsset());
        (await _client.DeleteAsync($"/api/assets/{a.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetFromJsonAsync<List<AssetDto>>("/api/assets"))!.Should().BeEmpty();
    }
}
