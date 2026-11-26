# Runbook: provider outage or rate limiting

Applies to MX, Quiltt and SimpleFIN. FlowLedger keeps working on existing data when a provider is down; only new syncs fail.

## Symptoms

- Sync returns 429 with `Retry-After`: the per-tenant minimum interval (`Sync__MinIntervalMinutes`, default 30 for real providers) or a provider rate limit; the provider was not called or asked you to wait.
- Syncs fail repeatedly with transient errors, or the provider reports "needs attention" for an institution (SimpleFIN returns messages naming the institution; the account may need re-authentication at the provider).
- Call budgets reached: nothing is sent once `Mx__Provider__DailyCallBudget` / `Mx__Provider__MonthlyCallBudget` is hit.

## Actions

1. Check the provider's status page and your credentials/keys (they may have expired or been revoked).
2. Do not hammer: manual retries inside the minimum interval are rejected anyway. To stop all MX calls immediately set `Mx__Provider__CallsPaused=true`.
3. Re-link the institution through the provider's flow if it asks for user action.
4. After recovery the next sync re-reads an overlap window (MX 30 days, Quiltt 14, SimpleFIN 7 by default) and fingerprint dedupe prevents duplicates, so missed days are caught up automatically. SimpleFIN caps a request at 90 days; for longer gaps use `SimpleFin__BackfillDays`.
5. If a webhook-driven sync was missed, the scheduled sync is the backstop.
6. If a mapping bug rather than an outage is the cause, raw payloads are archived and MX responses can be replayed without calling the provider (`Mx:Provider:ReplayArchivePath`, ADR 0007).

## What is not covered

No alerting is wired for repeated provider failure, and there is no dedicated repair queue for failed provider links; watch the Data & Backups page and logs.
