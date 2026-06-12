using System.Text.Json.Serialization;

namespace FlowLedger.Integrations.Mx.Contracts;

// ── Wire contracts ──────────────────────────────────────────────────────────────
//
// Internal record types that mirror the MX Platform API JSON payloads
// (https://docs.mx.com — Accept: application/vnd.mx.api.v1+json).
//
// These are the ONLY types in the assembly that know MX's snake_case wire shapes.
// Everything else operates on the provider-neutral DTOs from
// FlowLedger.Integrations.Abstractions. All property names map to MX's documented
// field names via [JsonPropertyName]; a source-generated JsonSerializerContext
// (see MxJsonContext) provides AOT-friendly, reflection-free (de)serialisation.

// ── User ────────────────────────────────────────────────────────────────────────

internal sealed record MxUser(
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("is_disabled")] bool? IsDisabled);

internal sealed record MxUserRequest(
    [property: JsonPropertyName("user")] MxUserBody User);

internal sealed record MxUserBody(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("metadata")] string? Metadata);

internal sealed record MxUserResponse(
    [property: JsonPropertyName("user")] MxUser? User);

// ── Member ──────────────────────────────────────────────────────────────────────

internal sealed record MxMember(
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("institution_code")] string? InstitutionCode,
    [property: JsonPropertyName("connection_status")] string? ConnectionStatus,
    [property: JsonPropertyName("connection_status_message")] string? ConnectionStatusMessage,
    [property: JsonPropertyName("is_being_aggregated")] bool? IsBeingAggregated,
    [property: JsonPropertyName("user_guid")] string? UserGuid);

internal sealed record MxMemberResponse(
    [property: JsonPropertyName("member")] MxMember? Member);

internal sealed record MxMemberRequest(
    [property: JsonPropertyName("member")] MxMemberBody Member);

internal sealed record MxMemberBody(
    [property: JsonPropertyName("institution_code")] string InstitutionCode);

// ── Connect widget ───────────────────────────────────────────────────────────────

internal sealed record MxWidgetRequest(
    [property: JsonPropertyName("widget_url")] MxWidgetBody WidgetUrl);

internal sealed record MxWidgetBody(
    [property: JsonPropertyName("widget_type")] string WidgetType,
    [property: JsonPropertyName("mode")] string? Mode);

internal sealed record MxWidget(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("user_id")] string? UserId);

internal sealed record MxWidgetResponse(
    [property: JsonPropertyName("widget_url")] MxWidget? WidgetUrl);

// ── Pagination ──────────────────────────────────────────────────────────────────

internal sealed record MxPagination(
    [property: JsonPropertyName("current_page")] int? CurrentPage,
    [property: JsonPropertyName("per_page")] int? PerPage,
    [property: JsonPropertyName("total_entries")] int? TotalEntries,
    [property: JsonPropertyName("total_pages")] int? TotalPages);

// ── Account ─────────────────────────────────────────────────────────────────────

internal sealed record MxAccount(
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("balance")] decimal? Balance,
    [property: JsonPropertyName("available_balance")] decimal? AvailableBalance,
    [property: JsonPropertyName("currency_code")] string? CurrencyCode,
    [property: JsonPropertyName("member_guid")] string? MemberGuid,
    [property: JsonPropertyName("user_guid")] string? UserGuid);

internal sealed record MxAccountsResponse(
    [property: JsonPropertyName("accounts")] IReadOnlyList<MxAccount>? Accounts,
    [property: JsonPropertyName("pagination")] MxPagination? Pagination);

// ── Transaction ─────────────────────────────────────────────────────────────────

internal sealed record MxTransaction(
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("account_guid")] string? AccountGuid,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("currency_code")] string? CurrencyCode,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("posted_at")] string? PostedAt,
    [property: JsonPropertyName("transacted_at")] string? TransactedAt,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("original_description")] string? OriginalDescription,
    [property: JsonPropertyName("merchant_guid")] string? MerchantGuid,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("top_level_category")] string? TopLevelCategory,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("type")] string? Type);

internal sealed record MxTransactionsResponse(
    [property: JsonPropertyName("transactions")] IReadOnlyList<MxTransaction>? Transactions,
    [property: JsonPropertyName("pagination")] MxPagination? Pagination);

// ── Webhook ─────────────────────────────────────────────────────────────────────

internal sealed record MxWebhookPayload(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("user_guid")] string? UserGuid,
    [property: JsonPropertyName("member_guid")] string? MemberGuid,
    [property: JsonPropertyName("account_guid")] string? AccountGuid,
    [property: JsonPropertyName("connection_status")] string? ConnectionStatus,
    [property: JsonPropertyName("completed_at")] long? CompletedAt,
    [property: JsonPropertyName("completed_on")] string? CompletedOn,
    [property: JsonPropertyName("member")] MxWebhookMember? Member);

internal sealed record MxWebhookMember(
    [property: JsonPropertyName("guid")] string? Guid,
    [property: JsonPropertyName("user_guid")] string? UserGuid,
    [property: JsonPropertyName("connection_status")] string? ConnectionStatus,
    [property: JsonPropertyName("connection_status_id")] int? ConnectionStatusId);
