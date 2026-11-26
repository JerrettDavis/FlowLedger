# Runbook: security incident / suspected data exposure

For operators of a self-hosted instance. This is a checklist, not a guarantee of any response time: FlowLedger has no
on-call, no automated detection and no breach-notification tooling. Vulnerability reports from outside follow
[SECURITY.md](../../SECURITY.md) (acknowledged within 48 hours, resolution timeline within 5 business days, best effort).

## 1. Contain

1. Note the time and what you observed. Preserve logs before restarting anything (`docker compose logs --no-color > incident.log`).
2. If a credential may be exposed, rotate it now (see [key-rotation.md](key-rotation.md)): `API__KEY`, `Api__TenantSigningKey`,
   OIDC client secret, provider keys and webhook secrets, the SimpleFIN access URL, `HomeAssistant__EncryptionKey` /
   `Integrations__EncryptionKey`, the restic password, the Postgres password.
3. If the host or the database is suspect, stop the stack (`docker compose --profile full down`; never `-v`) and isolate the host from the network.
4. Revoke provider links in the provider dashboards (MX, Quiltt, SimpleFIN Bridge) if bank data access may have been abused.

## 2. Assess

- Which tenants and which data (see [data inventory](../privacy/data-inventory-and-retention.md))? Transactions, balances, identity, raw archive, backups?
- There is no application audit log of user actions. Evidence comes from reverse-proxy access logs, container logs, the provider
  API call ledger (`provider_api_calls`, shown on Data & Backups) and the database itself.
- Logs are not redacted: treat captured logs as sensitive when sharing them.
- Check whether `DataAdmin__AllowRemote` was set and whether `/api/data/*` was reachable through the proxy.

## 3. Eradicate and recover

1. Fix the cause (patch, close the exposed port, remove the leaked credential).
2. If integrity is in doubt, restore into a clean host: [backups-and-recovery.md](../self-hosting/backups-and-recovery.md#disaster-recovery-new-machine-empty-database).
3. Bring the stack up, run **Back up now**, confirm the Data & Backups page is green.

## 4. Notify and record

- Notify affected users and any regulator as the law that applies to you requires; FlowLedger does not do this for you.
- Record timeline, root cause, data affected and follow-ups. Report a flaw in FlowLedger itself via [SECURITY.md](../../SECURITY.md).
