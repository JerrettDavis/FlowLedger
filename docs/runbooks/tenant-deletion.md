# Runbook: tenant deletion and data-subject requests

Covers a person asking to have their FlowLedger data deleted, in a multi-user instance. What is stored and for how long:
[data inventory and retention](../privacy/data-inventory-and-retention.md). The household (first) tenant cannot be deleted through
the application; see the last section.

## 1. Self-service (preferred)

The signed-in owner of the tenant sends, from the Web app's session or any caller that holds the tenant's credentials:

```
POST /api/account/delete-my-data
{"confirmation": "DELETE MY DATA"}
```

* The phrase must match exactly (case-sensitive, surrounding spaces ignored). Anything else returns 400 and changes nothing.
* No admin token is needed and the household admin is not involved: the tenant id comes from the verified tenant header, never the body.
* Order of work: refuse the household tenant (409) -> ask connectors to revoke (best effort) -> erase every row and stored file of the
  tenant, including its logins -> mark the tenant `deleted` in the registry -> write the `tenant.self_delete` audit event.
* The 200 response lists `rowsDeleted` per table, `filesDeleted` and `providers[]` with one status per enabled connector.

Check afterwards: `GET /api/data/tenants?status=deleted` (household admin) lists the tenant id; `audit_events` has a `tenant.self_delete`
row; the application log has "erased its own data" with counts only.

## 2. Provider follow-up

| `providers[].status` | Meaning | Action |
|---|---|---|
| `revoked` | Provider deleted the user and connections (MX) | none |
| `nothingtorevoke` | Provider held nothing for this tenant | none |
| `notsupported` | No revoke API in FlowLedger (Quiltt, SimpleFIN Bridge, Simulated) | Ask the person to remove the connection in the Quiltt dashboard or at the SimpleFIN Bridge, or do it for them |
| `failed` | The provider call failed (see the log line "Provider revocation ... failed") | Retry by hand in the provider dashboard; the erasure itself already happened |

## 3. Operator-initiated offboarding

For an orphaned tenant (no login maps to it any more) or a request made by someone who cannot sign in: the household admin lists
`GET /api/data/tenants?status=orphaned` and calls `POST /api/data/tenants/{id}/offboard` with `{"confirmTenantId": "<id>"}`. This does
not call providers: do section 2 by hand.

## 4. Backups age out; they are not rewritten

Erased data remains in existing backups until they expire (`Backup__Retention__*`, defaults: local snapshots 30, `premigrate-*` 90 days,
off-site 7 daily / 4 weekly / 6 monthly). Tell the requester the maximum window (about 6 months with the defaults) or shorten the keep
settings. To make every old copy unreadable at once, destroy the restic password file (this also destroys the ability to restore).
Never restore an old backup over a live database without re-running the deletion afterwards.

## 5. The household tenant

`delete-my-data` and offboarding both refuse it (409). To remove a single-household instance entirely: stop the stack, delete the database
volume, the raw archive volume (`objects_data`), the local backup directory and the restic repository, and revoke the provider links.
