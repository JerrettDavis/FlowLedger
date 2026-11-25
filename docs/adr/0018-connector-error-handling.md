# 0018 - Connector-neutral error handling

Status: accepted (contract merged; implementation in progress)

## Context
Every connector (SimpleFIN, MX, Quiltt, Simulated, future ones) can fail in the same handful of ways: the user must re-authenticate,
credentials are wrong, the bank is down, the aggregator network is down, the vendor's own service is down, we are throttled, the link was
removed, something internal broke. Today each connector throws its own mix of `ProviderException`s, only `NeedsUserAction` is handled
in one endpoint, retry/backoff is decided ad hoc, nothing is persisted, and the UI cannot tell a user *what to do*.

## Decision: one vocabulary, written once, consumed everywhere
```
connector-native status/error ──(connector maps, ONLY connector-specific code)──▶ ProviderIssue / ConnectionHealth   (Integrations.Abstractions)
                                                                                    │
        persisted per connection  ◀── FinancialSyncService / worker jobs ◀──────────┤   (Infrastructure, connector-agnostic)
        ProblemDetails + extensions ◀── one exception handler ◀─────────────────────┤   (Api, connector-agnostic)
        badge + repair CTA ◀── connections DTO ◀────────────────────────────────────┘   (Web, connector-agnostic)
```
* `ProviderIssueKind`: None, ReauthRequired, InvalidCredentials, InstitutionUnavailable, ProviderUnavailable, ServiceUnavailable,
  RateLimited, Disconnected, Internal, Unknown. `ProviderIssue` carries `Retryable`, `RequiresUserAction`, `RetryAfter`, `ProviderCode`.
* Every `ProviderException` has `Kind` (defaulting from its type; a connector refines it with an object initializer).
* `IFinancialDataProvider.GetConnectionHealthAsync` (default method derives from `GetConnectionStatusAsync`; connectors with richer state override).
* **Rule:** nothing outside a connector project may reference a connector's native status strings or error shapes. Anything that needs new
  behaviour for a new failure mode adds a `ProviderIssueKind` (or a policy keyed on one), never a connector `if`.

## Shared behaviour (implemented once, keyed on `ProviderIssue`)
| Concern | Rule |
|---|---|
| Persistence | `IProviderConnectionStore.RecordHealthAsync(provider, ConnectionIssueInfo?)`: set on failure (increments `ConsecutiveFailures`), cleared on success. Columns on `provider_connections`: `issue_kind, issue_message, issue_retryable, issue_requires_user_action, issue_retry_after, issue_at, consecutive_failures`. |
| Sync | `FinancialSyncService` catches `ProviderException`, classifies via `ProviderIssue.FromException`, records health, rethrows. Success records recovery. Never swallow. |
| Scheduled/on-demand jobs | Retryable -> back off (honour `RetryAfter`, otherwise exponential with cap, stop escalating at N failures); `RequiresUserAction` -> do NOT retry until the user acts, skip silently; Internal/Unknown -> bounded retries then stop and log. |
| HTTP | One `IExceptionHandler` maps any `ProviderException` to RFC 7807 ProblemDetails: 409 for RequiresUserAction, 429 for RateLimited (+`Retry-After`), 503 for Institution/Provider/ServiceUnavailable, 502 for Internal/Unknown. Extension members: `issueKind`, `retryable`, `requiresUserAction`, `retryAfterSeconds`, `provider`. Endpoints stop hand-rolling catches. |
| UI | Connection list returns `issue`. Badge by kind. `RequiresUserAction` shows a **Reconnect** button (provider-appropriate: Quiltt `Quiltt.reconnect(connectorId,{connectionId})`, others link out). Retryable shows "will retry automatically" with the time. Errors surfaced via the shared ProblemDetails reader, never string matching. |
| Secrets | Issue messages never contain tokens, API keys, account numbers or response bodies. |

## Quiltt sandbox as the reference simulation
Quiltt's Mock Sandbox Bank offers logins that simulate each failure mode (aggregator down, institution error, provider error, service error,
internal error, invalid credentials, multi-error, ...) and `connectionSimulateError` flips a connection to `ERROR_REPAIRABLE`. The Quiltt
connector maps each simulated scenario to a `ProviderIssueKind`; the mapping table lives in `docs/quiltt-sandbox-scenarios.md`. Because the downstream
layers only see `ProviderIssue`, the same handling covers MX, SimpleFIN and any future connector that maps its errors onto the contract.

## Testing
* Contract/taxonomy: `ProviderIssueTests` (Integrations.Tests).
* Connector mapping: per-connector unit tests with recorded/stubbed wire shapes, plus opt-in live tests (env-var gated) against sandboxes.
* Shared layers are tested with a fake connector that throws every `ProviderIssueKind`, proving behaviour is connector-independent.
