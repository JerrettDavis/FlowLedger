# Spreadsheet mode (bill sheet) — design

## Intent
The owner has kept a manual budget workbook for years: one row per bill/subscription/debt, columns
for payer, debt?, perpetual?, optional?, business/personal, auto/manual, account no., due day,
estimated vs actual cost, and an `x` per month when it is paid, with totals underneath (total,
optional, required, personal, business). Spreadsheet mode reproduces that grid in FlowLedger and
**fills in the `x` marks by itself** when a matching payment shows up in synced/imported transactions.

## Goals
- `/sheet` page that looks and scans like the workbook: sections (Bills, Credit Cards, per-person),
  month columns, click a cell to toggle paid, totals block, paid-vs-remaining for the month.
- Auto-update: after every sync/import (and on page load) unpaid cells flip to paid when a matching
  outflow transaction exists in that month; the actual cost is taken from the transaction.
- Manual override always wins and is never overwritten by automation.
- One-shot JSON import so the existing workbook can be loaded without retyping.

## Non-goals
- Not a replacement for forecasting / RecurringFlow occurrences (those stay as-is).
- No formulas/cell editing like a real spreadsheet; no xlsx export in this slice.

## Anti-goals
- Auto-marking the wrong payment silently. Matches are shown (transaction, amount) and one click un-marks.
- Account numbers leaking into logs or the repo.

## Approaches
1. Derive everything from RecurringFlow + PlannedFlowOccurrence — rejected: occurrences are never
   generated in production code, and the sheet needs flags (debt/optional/business…) flows lack.
2. Spreadsheet-only data in a JSON blob — rejected: can't query, no tenant guard.
3. **Chosen:** small `SheetBill` aggregate (+ owned `SheetPayment` per month), a matcher service that
   marks payments from transactions, API + page. Mirrors the Asset slice conventions.

## Decisions (my calls)
- Match rule: bill `MatchText` (case-insensitive substring of merchant/description; defaults to bill
  name) + outflow + date inside the month (±3 days into the neighbouring month for early/late
  pay) + amount within 35% of the estimate when an estimate exists. Each transaction pays one cell.
- Savings "deposit" rows with identical names are separate bills; no special casing.
- "Flux" due day = null due day.
- Month window shown: 6 months ending at the current month, navigable.

## Revisions after review (supersede the "Decisions" above where they differ)
- **Totals are per section** (JD bills, Sam expenses, ...) with a grand total, like the workbook:
  Total / Optional / Required / Personal / Business, estimated and actual.
- **Matching** is a pure function (`SheetMatcher`). Amount is never a filter (gas/electric swing
  seasonally), only a tie-breaker. Text comparison ignores case/punctuation/spacing; `MatchText`
  accepts alternatives (`att|at&t`), defaults to the bill name, and is editable inline.
- A transaction goes to the bill-month whose expected date (month + due day) is nearest (max 7 days
  away); "Flux" bills (no due day) match only inside the month. Candidates are sorted
  deterministically (distance, amount gap, date, ids) and assigned greedily from one pool, so
  identical rows (4 x $100 savings) and reruns are stable.
- Only `Debit` transactions are matched (the model has just Debit/Credit).
- Automatic marks are re-validated every run: one whose transaction vanished or no longer matches is
  cleared and rematched. Manual marks (including manual *unpaid*, which suppresses automation for
  that cell) are never touched.
- DB: unique (bill_id, month); unique filtered index on transaction_id so concurrent runs cannot
  double-assign.
- Auto-marker runs from `FinancialSyncService.SyncAsync` (covers Worker, API, on-demand), from the
  import commit, from `POST /api/sheet/refresh`, and as a backstop when the page loads.
- Import: CSV exported per Google Sheets tab, mapped by header name; month columns with `x` become
  manual paid marks; a section header row (`Sam Expenses`) starts a new section; `Total...` rows end
  a section. Account numbers are masked in API responses and never logged.
