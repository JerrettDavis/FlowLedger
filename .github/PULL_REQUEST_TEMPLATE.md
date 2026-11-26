## Summary

<!-- What changes and why. Link the issue. -->

## Tests

- [ ] Bug fix: a regression test that fails without the fix is included (name it): <!-- test name -->
- [ ] Feature: tests cover the new behaviour, including failure paths
- [ ] Docs-only or config-only change (no test needed; say why): <!-- reason -->

## Invariants touched

<!-- Tick everything this PR can affect and name the test that covers it. -->

- [ ] Tenant isolation (new or changed query, endpoint or store)
- [ ] Sync idempotency / cursor handling
- [ ] Transfer detection, money arithmetic, forecast or recurrence logic
- [ ] Webhook verification or replay protection
- [ ] None of the above

## Schema and migrations

- [ ] No schema change
- [ ] Migration included and exercised against a Postgres instance (not only the in-memory provider)

## Data and security

- [ ] No new logging of amounts, account numbers, names or credentials
- [ ] No secrets or personal financial data committed
- [ ] SECURITY.md and docs updated if a security-relevant behaviour changed
