# Provider response fixtures

Hand-written, sanitised copies of what each provider returns. They back the shared provider contract tests
(`Contract/SimpleFinProviderContractTests`, `Contract/QuilttProviderContractTests`) and the mapper snapshot tests
(`Contract/ProviderMapperSnapshotTests`, expected output in `Fixtures/snapshots/`).

The point of a fixture is to notice **provider schema drift**: when a provider changes a field name, type or shape,
the fixture-driven contract or snapshot test is where we find out, not production.

## Layout

| Path | Provider | Endpoint |
| --- | --- | --- |
| `simplefin/accounts.json` | SimpleFIN Bridge | `GET {access-url}/accounts?start-date=...&pending=1` |
| `quiltt/accounts.json` | Quiltt | GraphQL `FlowLedgerAccounts` query |
| `quiltt/transactions.json` | Quiltt | GraphQL transactions query |
| `snapshots/*.json` | (expected mapper output) | what `ProviderAccount` / `ProviderTransaction` must look like |

MX stubs still live inline in `Mx/MxWireMockFixture.cs`; moving them here is follow-up work.

## Capturing a new fixture (from a real response)

1. Call the provider with a sandbox or personal account and save the raw body (for SimpleFIN `curl` the access URL
   yourself; for Quiltt copy the response from the API client's debug log).
2. **Redact before committing** (a gitleaks pre-commit hook will also catch obvious secrets):
   - Replace the access URL / bearer token / API key and any `u:p@host` credentials: they must never appear.
   - Replace institution names, account names, merchant names and account numbers/masks with the `Example ...` style
     values used here. Keep the *shape*: field names, nesting, nullability, number-vs-string types.
   - Replace balances and amounts with plausible round-trip values, but keep the sign conventions (negative
     liability balances, negative debits) and decimal precision, because the mappers depend on both.
   - Timestamps: keep them as the provider formats them (SimpleFIN epoch seconds, Quiltt ISO dates). Do not use
     "now": fixtures must be deterministic.
3. Keep at least one of each edge the mapper handles: a pending item, a liability account, a transaction with a
   missing currency (Quiltt), a negative balance.
4. Re-run `dotnet test tests/FlowLedger.Integrations.Tests`. If a snapshot changes legitimately, regenerate with
   `UPDATE_SNAPSHOTS=1 dotnet test tests/FlowLedger.Integrations.Tests --filter ProviderMapperSnapshotTests`
   and review the diff in `Fixtures/snapshots/` like any other code change.

## Live tests

Tests tagged `[LiveFact("<ENV_VAR>")]` run only when the named environment variable is set (otherwise they report
as skipped with the reason), instead of a hard-coded `Skip`. A scheduled workflow that sets the secrets weekly and
opens an issue on failure is still to be added (workflow files are outside the scope of the commit that introduced these).
