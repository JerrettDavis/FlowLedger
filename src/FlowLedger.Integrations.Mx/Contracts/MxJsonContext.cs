using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowLedger.Integrations.Mx.Contracts;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for all MX wire contracts.
/// Provides reflection-free, AOT-friendly (de)serialisation. snake_case mapping is
/// expressed per-property via [JsonPropertyName] on the contract records, so no global
/// naming policy is required here.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MxUser))]
[JsonSerializable(typeof(MxUserRequest))]
[JsonSerializable(typeof(MxUserResponse))]
[JsonSerializable(typeof(MxMember))]
[JsonSerializable(typeof(MxMemberRequest))]
[JsonSerializable(typeof(MxMemberResponse))]
[JsonSerializable(typeof(MxWidgetRequest))]
[JsonSerializable(typeof(MxWidgetResponse))]
[JsonSerializable(typeof(MxAccountsResponse))]
[JsonSerializable(typeof(MxAccount))]
[JsonSerializable(typeof(MxTransactionsResponse))]
[JsonSerializable(typeof(MxTransaction))]
[JsonSerializable(typeof(MxWebhookPayload))]
internal sealed partial class MxJsonContext : JsonSerializerContext;
