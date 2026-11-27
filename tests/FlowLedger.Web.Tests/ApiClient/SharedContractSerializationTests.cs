using System.Reflection;
using System.Text.Json;
using FlowLedger.TestSupport.Builders;

namespace FlowLedger.Web.Tests.ApiClient;

/// <summary>
/// #96: contract DTOs now live once in FlowLedger.Contracts and are shared by the API and the Web client.
/// These tests pin the wire format: the API writes/reads with ASP.NET's web defaults (camelCase, case-insensitive) and the
/// Web client with its own case-insensitive options, so every shared type must round-trip across the two.
/// </summary>
public sealed class SharedContractSerializationTests
{
    private static readonly JsonSerializerOptions ApiOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions WebOptions = (JsonSerializerOptions)typeof(FlowLedger.Web.ApiClient.FlowLedgerApiClient)
        .GetField("JsonOptions", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static readonly AccountDto Account = AccountDtoBuilder.A().Build();

    [Fact]
    public void AccountDto_response_uses_camelCase_wire_names_and_reads_back_in_Web()
    {
        var json = JsonSerializer.Serialize(Account, ApiOptions);

        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            ["id", "name", "accountType", "balanceAmount", "balanceCurrency", "institution", "externalAccountRef", "isActive",
             "createdAt", "lastBalanceConfirmedAt", "typeLockedByUser", "mode", "hideTransactions", "version"],
            names);
        Assert.Equal(Account, JsonSerializer.Deserialize<AccountDto>(json, WebOptions));
    }

    [Fact]
    public void AccountDto_missing_optional_members_get_the_documented_defaults()
    {
        const string Json = """
            {"id":"11111111-1111-1111-1111-111111111111","name":"A","accountType":"Checking","balanceAmount":1,"balanceCurrency":"USD",
             "institution":null,"externalAccountRef":null,"isActive":true,"createdAt":"2026-01-02T03:04:05+00:00","lastBalanceConfirmedAt":null}
            """;

        var dto = JsonSerializer.Deserialize<AccountDto>(Json, WebOptions)!;

        Assert.False(dto.TypeLockedByUser);
        Assert.Equal("Standard", dto.Mode);
        Assert.False(dto.HideTransactions);
    }

    [Fact]
    public void Account_requests_serialized_by_Web_are_accepted_by_the_API_binder()
    {
        var create = CreateAccountRequestBuilder.A().Named("Savings").OfType("Savings").StartingAt(100m).At("Bank").Build();
        var settings = AccountSettingsRequestBuilder.A().Active(false).InMode("Holding").HidingTransactions().Build();
        var rename = UpdateAccountRequestBuilder.A().Named("New").Build();
        var retype = ChangeAccountTypeRequestBuilder.A().To("Credit").Build();

        Assert.Equal(create, JsonSerializer.Deserialize<CreateAccountRequest>(JsonSerializer.Serialize(create, WebOptions), ApiOptions));
        Assert.Equal(settings, JsonSerializer.Deserialize<AccountSettingsRequest>(JsonSerializer.Serialize(settings, WebOptions), ApiOptions));
        Assert.Equal(rename, JsonSerializer.Deserialize<UpdateAccountRequest>(JsonSerializer.Serialize(rename, WebOptions), ApiOptions));
        Assert.Equal(retype, JsonSerializer.Deserialize<ChangeAccountTypeRequest>(JsonSerializer.Serialize(retype, WebOptions), ApiOptions));
    }

    [Fact]
    public void TransactionDto_and_dashboard_activity_round_trip_between_API_and_Web_options()
    {
        var tx = TransactionDtoBuilder.A()
            .WithId(Guid.Parse("33333333-3333-3333-3333-333333333333"))
            .OnAccount(Guid.Parse("11111111-1111-1111-1111-111111111111"))
            .WithAmount(-5.5m)
            .WithDirection("Outflow")
            .Described("Coffee")
            .FromSource("Manual")
            .Unposted(new DateOnly(2026, 1, 2))
            .WithMerchant("Cafe")
            .WithCreatedAt(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero))
            .WithTags(["a"])
            .WithSplits([TransactionSplitDtoBuilder.A().WithAmount(1m).Build()])
            .Build();

        var json = JsonSerializer.Serialize(tx, ApiOptions);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal("Coffee", doc.RootElement.GetProperty("description").GetString());
            Assert.Equal("2026-01-02", doc.RootElement.GetProperty("effectiveDate").GetString());
        }

        var back = JsonSerializer.Deserialize<TransactionDto>(json, WebOptions)!;
        Assert.Equal(tx.Id, back.Id);
        Assert.Equal(["a"], back.Tags);
        Assert.Single(back.Splits!);

        var activity = DashboardActivityDtoBuilder.A()
                                   .WithRecentPosted([tx])
                                   .WithSuspicious([FlaggedTransactionDtoBuilder.A().WithTransaction(tx).Build()])
                                   .Build();
        var activityBack = JsonSerializer.Deserialize<DashboardActivityDto>(JsonSerializer.Serialize(activity, ApiOptions), WebOptions)!;
        Assert.Single(activityBack.RecentPosted);
        Assert.Equal(["big"], activityBack.Suspicious[0].Reasons);

        var create = CreateTransactionRequestBuilder.A().OnAccount(tx.AccountId).WithAmount(1m).WithDirection("Inflow").Described("x").On(new DateOnly(2026, 1, 2)).Build();
        Assert.Equal(create, JsonSerializer.Deserialize<CreateTransactionRequest>(JsonSerializer.Serialize(create, WebOptions), ApiOptions));
    }

    [Fact]
    public void CategoryDto_and_requests_round_trip_with_camelCase_names()
    {
        var category = CategoryDtoBuilder.A().Build();
        var json = JsonSerializer.Serialize(category, ApiOptions);

        Assert.Equal("""{"id":"22222222-2222-2222-2222-222222222222","path":"Food.Dining","displayName":"Dining","isSystem":false,"parentId":null,"version":0}""", json);
        Assert.Equal(category, JsonSerializer.Deserialize<CategoryDto>(json, WebOptions));

        var create = CreateCategoryRequestBuilder.A().AtPath("A.B").Named("B").Under(Guid.NewGuid()).Build();
        Assert.Equal(create, JsonSerializer.Deserialize<CreateCategoryRequest>(JsonSerializer.Serialize(create, WebOptions), ApiOptions));
    }
}
