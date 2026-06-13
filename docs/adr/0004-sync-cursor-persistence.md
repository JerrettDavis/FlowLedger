# ADR 0004 — Sync Cursor Persistence

## Context
MX syncs can be partial (the API returns a cursor for pagination). If the process restarts mid-sync, we need to resume rather than re-fetch everything.

## Decision
Each connected member's sync cursor is persisted in the SyncCursors table (added in Phase 2). The Quartz worker reads the cursor before each sync and writes the new cursor after success.

## Consequences
- Positive: Resilient to restarts; no duplicate imports on retry.
- Positive: Enables incremental sync (only new transactions since last cursor).
- Negative: Cursor state must be consistent with the transactions table; a failed partial sync leaves a gap until the next run.
