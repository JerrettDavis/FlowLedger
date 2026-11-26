# Quiltt sandbox scenarios -> connector-neutral issues

Reference mapping for [ADR 0018](adr/0018-connector-error-handling.md). Observations come from the real Quiltt SANDBOX
(Mock Sandbox Bank logins through the hosted Connector, state read over the Quiltt GraphQL API, 2026-11-25).
All Quiltt-specific strings live in `src/FlowLedger.Integrations.Quiltt/QuilttIssueMapper.cs`; nothing else in FlowLedger sees them.
Tests: `tests/FlowLedger.Integrations.Tests/Quiltt/QuilttIssueMappingTests.cs` (stubbed wire shapes, table-driven) and the opt-in
`QuilttLiveSandboxTests` (`QUILTT_LIVE_KEY`, `QUILTT_LIVE_PROFILE`, optional `QUILTT_LIVE_EXPECT`).

## Key finding: the Mock logins do not produce server-visible errors

Every Mock Sandbox Bank login that completes the mock OAuth "Authorize" page creates a Quiltt Connection with status `SYNCED`, accounts
and transactions, **including logins whose Connector UI says "We couldn't connect"**. The simulated failure lives only in the hosted
Connector flow (provider fallback exhausted). Nothing about it reaches the GraphQL API, so FlowLedger's server cannot distinguish them.
Real `ERROR_*` states reach us only through a live aggregator, a webhook, or the sandbox-only `connectionSimulateError` mutation.

### Login -> what the Connector showed -> what the API showed

| Login (Mock Sandbox Bank ...) | Connector UI | Connection created? / `status` | Provider issue FlowLedger can see |
|---|---|---|---|
| (base) | "All Done!" | yes, `SYNCED`, 2 accounts (DEPOSITORY, INVESTMENT) | None |
| - Aggregator Down | "We couldn't connect ... usually temporary" (no authorize page) | **no connection** | none (nothing persisted) |
| - Invalid Credentials | same message (no authorize page) | **no connection** | none |
| - Timeout (Pre-Widget) | same message (no authorize page) | **no connection** | none |
| - Error Institution | "All Done!" | yes, `SYNCED` (still `SYNCED` an hour later) | None |
| - Error Provider | "All Done!" | yes, `SYNCED` (same) | None |
| - Error Service | "All Done!" | yes, `SYNCED` (same) | None |
| - Internal Error | authorize page, then "We couldn't connect" | yes, `SYNCED` | None |
| - Multi Errors (Failure) | authorize page, then "We couldn't connect" | yes, `SYNCED` | None |
| - Timeout (Post-Widget) | authorize page, then "We couldn't connect" | yes, `SYNCED` | None |
| - Multi Errors (Successful) | "Let's try a different connection" -> Continue -> second authorize -> "All Done!" | **two** connections, both `SYNCED` | None |

The six "no server-visible signal" rows (Aggregator Down, Invalid Credentials, Internal Error, Multi Errors (Failure), both Timeouts)
produced identical Connector text and no discriminating code could be captured (the Connector's own network traffic is not visible
from the page). Behaviour for FlowLedger: nothing is persisted and the user sees the Connector message. A future change that reads the
Connector exit event (Web interop JS reporting `exited.*` metadata to an Api endpoint that calls into the Quiltt project) could map those
to `InvalidCredentials` / `ProviderUnavailable` / `Internal`; no such code exists yet.

## Connection status -> ProviderIssueKind (the mapping FlowLedger implements)

"Observed" = seen live; "schema" = taken from the Quiltt GraphQL `ConnectionStatus` enum and its descriptions (no sandbox login produced it).

| Quiltt `Connection.status` | Evidence | ProviderIssueKind | Retryable | User action | FlowLedger behaviour |
|---|---|---|---|---|---|
| `SYNCED` | observed | None | - | - | healthy; clear any stored issue |
| `SYNCING`, `INITIALIZING`, `UPGRADING` | schema | None (lifecycle `Syncing`) | - | - | not a problem; keep reading |
| `ERROR_REPAIRABLE` | observed via `connectionSimulateError` (returns `success:true`, record `status:"ERROR_REPAIRABLE"`; data stays readable, stale) | ReauthRequired | no | yes | stop retrying; show **Reconnect** (`Quiltt.reconnect(connectorId,{connectionId})`) |
| `ERROR_INSTITUTION` | schema ("provider reports an error from the user's Institution; Quiltt keeps retrying") | InstitutionUnavailable | yes | no | back off and retry; "bank is having trouble" |
| `ERROR_PROVIDER` | schema ("provider reports an error with the Connection; Quiltt keeps retrying") | ProviderUnavailable | yes | no | back off and retry; "aggregator network issue" |
| `ERROR_SERVICE` | schema ("Quiltt internal error; team notified; retried automatically") | ServiceUnavailable | yes | no | back off and retry; "vendor outage" |
| `DISCONNECTING` -> `DISCONNECTED` | observed via `connectionDisconnect` (immediate record `DISCONNECTING`, `DISCONNECTED` within seconds; accounts stay `OPEN` with stale balances) | Disconnected | no | yes | user must re-link (Reconnect on a DISCONNECTED id creates a NEW connection) |
| anything else | n/a | Unknown | no | no | surfaced as unclassified |

Profile-level rules (`QuilttIssueMapper.Aggregate` / `BlockingException`):
* The worst issue across the profile's connections wins: user-action kinds (Reauth, InvalidCredentials, Disconnected) first, then
  Internal, ServiceUnavailable, ProviderUnavailable, InstitutionUnavailable, RateLimited, Unknown.
* A `DISCONNECTED` connection only counts when the profile has no other (live) connection; Quiltt keeps them forever.
* No connections = `ConnectionPending`. Any `SYNCING` with no issue = `Syncing`.
* Reads (`GetAccountsAsync`) throw the typed exception only when NO connection is usable; with a mix, healthy data still syncs and the
  broken connection is reported through `GetConnectionHealthAsync`.
* The Quiltt connection id travels in `ProviderIssue.ProviderCode` as `"<STATUS>:<connectionId>"`; parse with the public
  `QuilttProviderCode.TryGetConnectionId`.

## HTTP / GraphQL failures -> exceptions

| Wire shape (observed unless noted) | Exception (`Kind`) | Retry |
|---|---|---|
| Session API 401 `{"message":"Not Authenticated","instruction":"...valid API Key secret..."}` (bad/revoked server key) | Fatal (`Internal`) | operator fixes config |
| GraphQL 401 same body, "...valid and unexpired Session token..." (bad/expired token) | Transient (`Internal`) | next sync mints a new session |
| 403 (docs only: calls on an `ERROR_REPAIRABLE` connection) | NeedsUserAction (`ReauthRequired`) | no |
| 429 `{"message":"Too Many Requests","instruction":"...Sessions you can create per hour for this Profile..."}`, **no `Retry-After` header** | RateLimited (`RateLimited`), `RetryAfter` = header, else `Quiltt:RateLimitFallback` (15 min) | yes, after the delay |
| HTTP 500 | Transient (`Internal`) | bounded |
| HTTP 502/503/504/408, DNS/TLS/timeout | Transient (`ServiceUnavailable`) | yes |
| GraphQL 200 + `errors[]` with `extensions.code` `NOT_FOUND` (unknown / disconnected connection id in a mutation) | Fatal (`Internal`) | no |
| GraphQL 200 + `errors[]` validation codes `undefinedField`, `argumentNotAccepted`, `selectionMismatch` (our query is wrong) | Fatal (`Internal`) | no (bug) |
| `connectionSimulateError` on a non-`SYNCED` connection: 200, payload `{"success":false,"errors":[{"message":"Only Connections with status of INITIALIZING or SYNCED can be put in a repairable error state"}]}` | not a read path | n/a |

## Webhooks (docs only; no subscription exists in this sandbox, so none were observed)
`connection.synced.successful[.initial|.historical]` (recovery), `connection.synced.errored.repairable` (-> ReauthRequired),
`connection.synced.errored.institution` (-> InstitutionUnavailable, Finicity per changelog), `connection.syncing`. Deliveries are batched
under `events[]`; deduplicate on `event.id`.

## Schema corrections found while verifying (applied)
The pre-existing queries had never run against Quiltt: `accounts` and `connections` are plain lists (no `first`/`after`, no
`nodes`/`pageInfo`), accounts expose `kind` (`DEPOSITORY|INVESTMENT|CREDIT|LOAN|INSURANCE`) and `state`, not `type`. `transactions`
is a real paged connection and its query was already correct.

## Operational notes
* Quiltt limits **sessions per profile per hour**; ~10 sessions in 8 minutes triggered HTTP 429 for ~60 minutes. Mint sessions sparingly
  (cache per profile until expiry); health check, sync and webhook-triggered syncs each mint their own today.
