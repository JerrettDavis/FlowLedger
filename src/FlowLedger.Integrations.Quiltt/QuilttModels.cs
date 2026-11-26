using System.Text.Json.Serialization;

namespace FlowLedger.Integrations.Quiltt;

/// <summary>A Quiltt session: a short-lived token scoped to one profile, safe to hand to the browser Connector.</summary>
public sealed record QuilttSession(string Token, string ProfileId, DateTimeOffset? ExpiresAt);

internal sealed class QSessionResponse
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("userId")] public string? UserId { get; set; }
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
}

internal sealed class QGraphQlResponse<T>
{
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("errors")] public List<QGraphQlError>? Errors { get; set; }
}

internal sealed class QGraphQlError
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("extensions")] public Dictionary<string, System.Text.Json.JsonElement>? Extensions { get; set; }
}

internal sealed class QPageInfo
{
    [JsonPropertyName("hasNextPage")] public bool HasNextPage { get; set; }
    [JsonPropertyName("endCursor")] public string? EndCursor { get; set; }
}

internal sealed class QConnection<T>
{
    [JsonPropertyName("nodes")] public List<T>? Nodes { get; set; }
    [JsonPropertyName("pageInfo")] public QPageInfo? PageInfo { get; set; }
}

// Verified against the live schema: Query.accounts / Query.connections are plain lists (no first/after, no nodes/pageInfo);
// only transactions, holdings and statements are paged connections.
internal sealed class QAccountsData { [JsonPropertyName("accounts")] public List<QAccount>? Accounts { get; set; } }
internal sealed class QConnectionsData { [JsonPropertyName("connections")] public List<QConnectionNode>? Connections { get; set; } }

/// <summary>A Quiltt Connection (one linked institution login) as read for health classification.</summary>
internal sealed class QConnectionNode
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
    [JsonPropertyName("provider")] public string? Provider { get; set; }
}
internal sealed class QTransactionsData { [JsonPropertyName("transactions")] public QConnection<QTransaction>? Transactions { get; set; } }

internal sealed class QAccount
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("mask")] public string? Mask { get; set; }
    [JsonPropertyName("currencyCode")] public string? CurrencyCode { get; set; }
    [JsonPropertyName("balance")] public QBalance? Balance { get; set; }
    [JsonPropertyName("institution")] public QInstitution? Institution { get; set; }
    [JsonPropertyName("connection")] public QConnectionRef? Connection { get; set; }
}

internal sealed class QBalance
{
    [JsonPropertyName("current")] public decimal? Current { get; set; }
    [JsonPropertyName("available")] public decimal? Available { get; set; }
}

internal sealed class QInstitution { [JsonPropertyName("name")] public string? Name { get; set; } }

internal sealed class QConnectionRef
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

internal sealed class QTransaction
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("amount")] public decimal? Amount { get; set; }
    [JsonPropertyName("currencyCode")] public string? CurrencyCode { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("entryType")] public string? EntryType { get; set; }
    [JsonPropertyName("account")] public QAccountRef? Account { get; set; }
}

internal sealed class QAccountRef { [JsonPropertyName("id")] public string? Id { get; set; } }
