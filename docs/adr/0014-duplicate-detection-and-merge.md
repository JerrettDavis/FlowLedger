# ADR 0014 — Duplicate detection, suggested merges and safe auto-merge

Status: accepted (2026-11-23)

## Context

Ingest already dedupes exactly (a fingerprint per bank id, or per account/date/amount/description for CSV) and the sync absorbs an
unambiguous pending-to-posted re-issue. What it cannot see: a CSV that overlaps rows the bank already synced (different fingerprint
schemes), a pending row whose posted twin arrived under a new id when several candidates exist, and re-formatted re-imports.
Measured on the household data (primary tenant, 1,147 rows): of 71 same-account/amount pairs within 3 days, nearly all were two distinct
bank ids (a vending machine used twice, a recurring $55 service), so a naive "same amount + date" rule would destroy real data.

## Decision

1. `DuplicateDetector` (pure) pairs rows only on the **same account, direction and amount**, at most 3 days apart (7 for pending to posted).
2. **High** (auto-merged): bank row vs imported/manual row with a similar descriptor; a pending ghost of a posted row; two un-id'd rows
   with a near-identical descriptor on one day. **Possible** (suggest only): everything else, notably two rows the bank itself gave
   different ids, and any pair where a row could match several others.
3. A row matched to a plan or reconciled is never the one removed; if both are, nothing is proposed.
4. Merging folds category (a manual one beats an auto one), merchant name, notes, splits and the posted date into the survivor, then
   removes the other row in one DB transaction that also writes a **tombstone** (its fingerprint) and a JSON snapshot to `merged_transactions`.
   Sync and CSV import consult the tombstones, so a merged-away row can never be re-created. The log is visible at `GET /api/duplicates/log`.
5. Auto-merge runs after each sync that added rows (before categorization) and after each CSV import; `Duplicates:AutoMerge=false` turns it
   off (suggestions remain). Dismissed pairs are remembered (`duplicate_dismissals`) and never suggested again.
6. Tables are ad-hoc (`CREATE TABLE IF NOT EXISTS`, registered in `AdHocSchema`), so backups, exports and restore drills carry them.

## Not done

- Cross-tenant duplicates (the second household tenant mirrors 735 rows of the first) are deliberately not merged: tenants are isolation boundaries.
- A one-click undo: the snapshot is stored, but re-creating the aggregate from it is manual for now.
- Duplicate accounts and duplicate merchants/categories are handled elsewhere (category move/merge, merchant aliases).

API: `GET /api/duplicates`, `POST /api/duplicates/{auto-merge,merge,dismiss}`, `GET /api/duplicates/log`. UI: Organize > Duplicates.
