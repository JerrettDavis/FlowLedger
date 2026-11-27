using System.Net;
using System.Net.Http.Json;
using FlowLedger.TestSupport.Builders;
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
        CreateAssetRequestBuilder.A().Named(name).InCategory(category).Worth(value).ValuedOn(valuedOn)
            .PurchasedOn(new DateOnly(2024, 1, 1), price).MadeBy("Apple", "MBP 14").WithSerial("SN-SECRET-1").WithNotes("note")
            .Valued(method, rate).LinkedTo(link).WithAttributes(attrs).Build();

    private async Task<AssetDto> Create(CreateAssetRequest r)
    {
        var resp = await _client.PostAsJsonAsync("/api/assets", r);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<AssetDto>())!;
    }

    private async Task<AccountDto> CreateAccount(string name, string type, decimal balance, HttpClient? client = null)
    {
        var resp = await (client ?? _client).PostAsJsonAsync("/api/accounts",
            CreateAccountRequestBuilder.A().Named(name).OfType(type).StartingAt(balance).Build());
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
            RecordValuationRequestBuilder.A().Worth(1500m).AsOf(factory.ServerToday).From("Appraiser").WithNote("Insurance appraisal").Build());
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var third = await _client.PostAsJsonAsync($"/api/assets/{a.Id}/valuations",
            RecordValuationRequestBuilder.A().Worth(1400m).AsOf(factory.ServerToday).Build());
        third.StatusCode.Should().Be(HttpStatusCode.OK);

        var reread = await _client.GetFromJsonAsync<AssetDto>($"/api/assets/{a.Id}");
        // Blending: latest valuation per distinct source within +/-7 days is averaged:
        // Appraiser 1500 and the latest Manual (1400, not the original 2000) => 1450.
        reread!.CurrentValue.Should().Be(1450m);
        reread.Valuations.Should().HaveCount(3);
        reread.Valuations!.Select(v => v.Source).Should().Contain(["Appraiser", "Manual"]);
    }

    [Fact]
    public async Task Editing_a_custom_field_persists()
    {
        var a = await Create(NewAsset("Rifle", "Firearm", 800m, attrs: new() { ["Caliber"] = ".22" }));

        var upd = UpdateAssetRequestBuilder.A().Named("Rifle").InCategory("Firearm").PurchasedOn(null, 800m).MadeBy("Ruger", "10/22").WithSerial("SN-2")
            .WithAttributes(new() { ["Caliber"] = ".223", ["Action"] = "Semi" }).Build();
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
        (await _client.PostAsJsonAsync($"/api/assets/{Guid.NewGuid()}/valuations", RecordValuationRequestBuilder.A().Worth(1m).Build()))
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
            RecordValuationRequestBuilder.A().Worth(320000m).From("Appraiser").Build())).EnsureSuccessStatusCode();

        var account = await _client.GetFromJsonAsync<AccountDto>($"/api/accounts/{acct.Id}");
        // Initial Manual 300000 and Appraiser 320000 (same day, different sources) blend to 310000.
        account!.BalanceAmount.Should().Be(310000m);
        (await Summary()).NetWorth.NetWorth.Should().Be(310000m);
    }

    [Fact]
    public async Task Valuations_from_different_sources_within_window_are_blended_and_outside_window_latest_wins()
    {
        var today = factory.ServerToday;
        var a = await Create(NewAsset(value: 1000m, valuedOn: today));

        // Different source, same window: average of 1000 (Manual) and 2000 (Appraiser).
        var blended = await _client.PostAsJsonAsync($"/api/assets/{a.Id}/valuations",
            RecordValuationRequestBuilder.A().Worth(2000m).AsOf(today).From("Appraiser").Build());
        blended.EnsureSuccessStatusCode();
        (await blended.Content.ReadFromJsonAsync<AssetDto>())!.CurrentValue.Should().Be(1500m);

        // Same source twice counts once (latest per source), so the feed does not outweigh others.
        (await _client.PostAsJsonAsync($"/api/assets/{a.Id}/valuations",
            RecordValuationRequestBuilder.A().Worth(3000m).AsOf(today).From("Appraiser").Build())).EnsureSuccessStatusCode();
        (await _client.GetFromJsonAsync<AssetDto>($"/api/assets/{a.Id}"))!.CurrentValue.Should().Be(2000m);

        // A valuation dated more than 7 days later leaves earlier ones outside the window: latest wins.
        (await _client.PostAsJsonAsync($"/api/assets/{a.Id}/valuations",
            RecordValuationRequestBuilder.A().Worth(500m).AsOf(today.AddDays(8)).From("Manual").Build())).EnsureSuccessStatusCode();
        (await _client.GetFromJsonAsync<AssetDto>($"/api/assets/{a.Id}"))!.CurrentValue.Should().Be(500m);
    }

    private async Task<decimal> Balance(Guid accountId)
        => (await _client.GetFromJsonAsync<AccountDto>($"/api/accounts/{accountId}"))!.BalanceAmount;

    private static UpdateAssetRequest ToUpdate(AssetDto a, Guid? link) =>
        UpdateAssetRequestBuilder.A().Named(a.Name).InCategory(a.Category).PurchasedOn(a.PurchaseDate, a.PurchasePrice)
            .MadeBy(a.Manufacturer, a.Model).WithSerial(a.SerialNumber).WithNotes(a.Notes)
            .Valued(a.ValuationMethod, a.AnnualRate, a.SalvageValue, a.UsefulLifeYears).LinkedTo(link)
            .WithAttributes(new Dictionary<string, string>(a.Attributes)).Build();

    [Fact]
    public async Task Unlinking_a_manual_asset_account_zeroes_it_so_net_worth_is_not_doubled()
    {
        var acct = await CreateAccount("House value", "ManualAsset", 0m);
        var house = await Create(NewAsset("House", "RealEstate", 300000m, link: acct.Id));
        (await Balance(acct.Id)).Should().Be(300000m);

        (await _client.PutAsJsonAsync($"/api/assets/{house.Id}", ToUpdate(house, null))).EnsureSuccessStatusCode();

        (await Balance(acct.Id)).Should().Be(0m);
        var s = await Summary();
        s.NetWorth.NetWorth.Should().Be(300000m);
        s.NetWorth.PhysicalAssets.Should().Be(300000m);
    }

    [Fact]
    public async Task Relinking_between_manual_asset_accounts_moves_the_value()
    {
        var a = await CreateAccount("A", "ManualAsset", 0m);
        var b = await CreateAccount("B", "ManualAsset", 0m);
        var house = await Create(NewAsset("House", "RealEstate", 300000m, link: a.Id));

        (await _client.PutAsJsonAsync($"/api/assets/{house.Id}", ToUpdate(house, b.Id))).EnsureSuccessStatusCode();

        (await Balance(a.Id)).Should().Be(0m);
        (await Balance(b.Id)).Should().Be(300000m);
        (await Summary()).NetWorth.NetWorth.Should().Be(300000m);
    }

    [Fact]
    public async Task Deleting_a_linked_asset_removes_its_value_from_the_account_and_net_worth()
    {
        var acct = await CreateAccount("House value", "ManualAsset", 0m);
        var house = await Create(NewAsset("House", "RealEstate", 300000m, link: acct.Id));

        (await _client.DeleteAsync($"/api/assets/{house.Id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Balance(acct.Id)).Should().Be(0m);
        (await Summary()).NetWorth.NetWorth.Should().Be(0m);
    }

    [Fact]
    public async Task Two_assets_on_one_manual_asset_account_sum_instead_of_overwriting()
    {
        var acct = await CreateAccount("Vehicles", "ManualAsset", 0m);
        var car = await Create(NewAsset("Car", "Vehicle", 20000m, link: acct.Id));
        await Create(NewAsset("Boat", "Vehicle", 30000m, link: acct.Id));
        (await Balance(acct.Id)).Should().Be(50000m);
        (await Summary()).NetWorth.NetWorth.Should().Be(50000m);

        (await _client.PostAsJsonAsync($"/api/assets/{car.Id}/valuations", RecordValuationRequestBuilder.A().Worth(18000m).Build()))
            .EnsureSuccessStatusCode();
        (await Balance(acct.Id)).Should().Be(48000m);
        (await Summary()).NetWorth.NetWorth.Should().Be(48000m);
    }

    [Fact]
    public async Task Two_assets_sharing_a_mortgage_subtract_the_debt_once_in_the_summary()
    {
        var mortgage = await CreateAccount("Mortgage", "Mortgage", 200000m);
        await Create(NewAsset("House", "RealEstate", 300000m, link: mortgage.Id));
        await Create(NewAsset("Garage", "RealEstate", 50000m, link: mortgage.Id));

        var s = await Summary();
        s.LinkedLiabilities.Should().Be(200000m);
        s.TotalEquity.Should().Be(150000m);
        s.NetWorth.NetWorth.Should().Be(150000m);
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
            valuedOn: factory.ServerToday.AddYears(-1)));

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
