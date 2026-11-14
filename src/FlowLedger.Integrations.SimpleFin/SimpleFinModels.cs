using System.Text.Json.Serialization;

namespace FlowLedger.Integrations.SimpleFin;

internal sealed class SfResponse
{
    [JsonPropertyName("errors")] public List<string>? Errors { get; set; }
    [JsonPropertyName("accounts")] public List<SfAccount>? Accounts { get; set; }
}

internal sealed class SfOrg
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("domain")] public string? Domain { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class SfAccount
{
    [JsonPropertyName("org")] public SfOrg? Org { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("balance")] public string? Balance { get; set; }
    [JsonPropertyName("available-balance")] public string? AvailableBalance { get; set; }
    [JsonPropertyName("transactions")] public List<SfTransaction>? Transactions { get; set; }
}

internal sealed class SfTransaction
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("posted")] public long Posted { get; set; }
    [JsonPropertyName("amount")] public string? Amount { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("transacted_at")] public long? TransactedAt { get; set; }
    [JsonPropertyName("pending")] public bool? Pending { get; set; }
}
