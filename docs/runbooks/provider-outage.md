# Runbook: provider outage or auth failure

Symptoms: syncs fail repeatedly, stale balances, `ProviderAuthException`/401/403 in worker logs, the account's
"re-login required" state in the UI.

1. Check the provider status page (MX, Quiltt, SimpleFIN) to separate an outage from a credential problem.
2. **Outage:** do nothing destructive. Failed syncs do not delete data; the next successful sync backfills. The minimum sync interval (`Sync__MinIntervalMinutes`, default 30 for real connectors) prevents retry storms. Pause the scheduled sync with `JobSchedule__Enabled=false` if the provider asks you to stop calling.
3. **Auth failure:** re-link the connection from the Connectors page (or claim a new SimpleFIN setup token). The raw payload archive keeps everything fetched before the failure.
4. **Billing risk (MX):** every call is billable. Check the provider call ledger before re-enabling a tight schedule.
5. After recovery trigger one manual sync, confirm the last-synced time advances, and re-enable the schedule.
6. Record the incident (start, end, cause) so the SLO note in [slo-and-capacity](../self-hosting/slo-and-capacity.md) can be reviewed.
