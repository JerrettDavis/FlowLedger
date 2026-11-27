# 0020 - IFinancialDataProvider stays one interface (no per-capability split yet)

Status: accepted

## Context
Issue #100 asked whether `IFinancialDataProvider` should be split by capability (for example `IAccountReader`, `ITransactionReader`,
`IWebhookVerifier`, `IConnectionLifecycle`) instead of one interface whose optional parts are advertised through
`ProviderCapabilities` and `ConnectorTraits`.

What exists today:

- One interface with metadata, connection lifecycle, accounts, transactions, webhook verification and parsing, and member resolution.
  Optional members are either default interface members (`GetConnectionHealthAsync`, `Traits`) or advertised by
  `ProviderCapabilities` flags that callers check before calling.
- Four connectors implement it (MX, Quiltt, SimpleFIN, Simulated) and every one of them is **keyed by connector id** in DI and listed by
  `IConnectorRegistry`; the three HTTP connectors (MX, Quiltt, SimpleFIN) are also wrapped by the same call-ledger and budget handler (`ConnectorCallGuardHandler`; Simulated has no HTTP client), and driven by one
  `FinancialSyncService` pipeline that treats a connector as a single unit (accounts, then transactions, then health).
- The webhook endpoints, the on-demand sync job, the account-link store and the status endpoints all resolve a provider by id and
  then call two or three of its operations.

## Decision
Keep the single interface. Do not split it now.

Reasons:

1. **No consumer wants a part of a provider.** Every caller resolves a connector by id and uses it as a unit. A split would add four
   registrations per connector and force every caller to resolve several services for the same id, with no caller that benefits from
   depending on fewer members.
2. **The gaps are already handled where they matter.** Capability flags and default members mean a connector without webhooks or health
   simply does not advertise them; the sync pipeline and webhook endpoint already check the flag. The split would move the same check
   from a flag to a type test.
3. **It is not low-risk.** It changes the Abstractions API that all four connectors, the registry, the call-guard wiring, the sync
   service, the webhook endpoints and the test doubles share, and the ledger/budget/kill-switch handler is keyed per connector, not per
   capability. A mistake would not fail loudly: a connector registered without its guard would call a billable provider unmetered.
4. **The real defects in the issue are fixed without it.** Shared HMAC comparison (`ConstantTime`), the connector call guard for all
   HTTP connectors, `ConnectorId`, options bound with `ValidateOnStart`, and `TimeProvider` seams are done and independent of the split.

## Consequences
- New connectors keep implementing one interface; optional behaviour is a capability flag plus a default member.
- A part of the interface can still be extracted later without a break: a new narrow interface (for example for webhook verification)
  that the existing connector classes also implement, adopted by one consumer at a time. Revisit when a consumer appears that needs
  only one capability (for example a verify-only webhook receiver that must not hold a database context), or when a fifth connector
  would implement fewer than half of the members.
- The call guard must stay applied per connector id whatever shape the interface takes (it is registered through `AddConnectorCallGuard`
  on each connector's typed HttpClient; there is no architecture test for it yet, which is the more useful follow-up than a split).
