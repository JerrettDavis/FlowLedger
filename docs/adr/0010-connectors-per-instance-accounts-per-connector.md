# ADR 0010 — Connectors enabled per instance, linked per account

Status: Accepted (implementation staged; see "Rollout")

## Context
Until now exactly one provider (Simulated, SimpleFIN Bridge or MX) was selected for the whole process and every account synced
through it (ADR 0003, ADR 0009). We want the operator to enable several connectors at once (SimpleFIN, MX, later Quiltt/Plaid-style
aggregators), and for each *account* to be linked to one connector: a checking account via SimpleFIN, a card via MX, a manual
account via none.

## Model
- **Connector** = a provider implementation plus its credentials. **Enabled per instance** by configuration
  (`SimpleFin:Enabled`, `Mx:Enabled`, ...). Simulated is enabled when no real connector is, or explicitly via
  `Connectors:Simulated=true`. Several real connectors may be enabled together.
- **Scope.** Today every real connector uses operator-held credentials and is usable by the household tenant only. A connector
  descriptor carries that scope so a future per-user connector (user brings their own aggregator login) can be usable by any tenant.
- **Account link.** `account_connector_links(tenant_id, account_id, connector_id, provider_account_id)`:
  an account has at most one link; a provider account is linked to at most one account. No link = manual account (never synced).
  The table is created on first use like the other ad-hoc stores and is part of backups/exports.
- **Sync** runs once per enabled, usable connector and merges the results. For each provider account:
  1. a link for `(connector, provider_account_id)` selects the domain account;
  2. otherwise the legacy match (`Account.Institution == provider_account_id`, how accounts were linked before this ADR) selects it
     and the link is recorded (lazy backfill; no data migration, nothing changes for existing accounts);
  3. otherwise a new account is created and linked (current behaviour);
  4. if the selected account is linked to a *different* connector, this provider account is skipped for this connector.
- **Cursors, balance history, call ledger, budgets** are already keyed by provider name, so they stay per connector.
- **Failure isolation.** One connector failing must not stop the others. If every connector fails the first error is rethrown, so a
  single-connector install behaves exactly as before; if some succeed the failures are logged and the partial result returned.

## Compatibility rules
- With one connector enabled behaviour is unchanged.
- The un-keyed `IFinancialDataProvider` remains the *primary* connector (SimpleFIN, then MX, then Simulated) for older call sites;
  new code resolves connectors by id through `IConnectorRegistry`.
- Real connectors are still household-only; other tenants keep the "no linked institution" behaviour.

## Rollout
1. Registry + keyed provider registration + removal of the "SimpleFIN and MX are mutually exclusive" rule. 
2. Link store and per-connector sync in `FinancialSyncService`; composite sync across connectors.
3. API/UI: Connectors page shows enabled/active per connector and accounts per connector; Accounts shows each account's connector
   and lets the owner move an account to another connector (choosing among the provider accounts discovered by that connector).

## Consequences
- Sync cost scales with the number of enabled connectors; the existing per-tenant minimum interval and per-connector call budgets still apply.
- Duplicate real-world accounts (the same account visible via SimpleFIN and MX) are resolved by linking, not by merging data.
- Resolves ADR 0009 items 1, 3 (connector descriptor), 4 (several connections per tenant), 5 (household guard per connector scope).
  Still open from ADR 0009: per-user credentials/consent, webhooks routed per connection, user/tenant deletion revoking consent.
