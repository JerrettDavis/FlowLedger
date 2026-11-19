# ADR 0011 — Account tracking settings (enabled, mode, hidden transactions)
Status: Accepted and implemented

## Context
Every account fed every screen. Investment accounts in particular produce buy/sell/dividend lines that drown the
Transactions list, dashboard, spending breakdown and reports, yet their balance matters for net worth. `IsActive`
existed but only dropped an account from the account list: its transactions still counted and it still synced.

## Model
Three per-account settings, edited in the account's Edit dialog ("Tracking & reporting"):

| Setting | Values | Effect |
|---|---|---|
| **Enabled** (`IsActive`) | on / off | Off = stop tracking entirely: sync skips it (no snapshots, transactions or flows), it leaves every list, total, report, forecast and net worth. Data is kept; turning it on resumes. |
| **Mode** (`Mode`) | `Standard` / `Holding` | Holding = balance tracking for investments, retirement and similar. Counts toward net worth, shows balance history, and can be opened/filtered to see its activity, but its transactions stay out of everyday views. |
| **Hide transactions** (`HideTransactions`) | on / off | Same hiding for any account without changing its mode. Always on for holdings. |

`Account.TransactionsHidden = HideTransactions || Mode == Holding`. New `Investment` accounts default to `Holding`;
the migration set existing Investment accounts to `Holding`.

## Where hiding applies
"Everyday views" = the default (unfiltered) Transactions list, dashboard activity and anomalies, spending breakdown,
query/report datasets (and so budgets/alerts), and recurring-flow detection. Disabled accounts are excluded from all of
these always. Hidden accounts are excluded unless the caller selects them explicitly (Transactions account filter or
"Include hidden accounts", Spending account multi-select, `?accountId=` / `?includeHidden=true`).

Net worth, balance history, investment performance, categorisation and import still see every enabled account.

## Transfers
Hiding is applied AFTER transfer detection (`AccountVisibility.ExcludedIdsAsync`), so a checking-to-brokerage move
still pairs with its hidden leg and is not counted as spending.

## API
`PUT /api/accounts/{id}/settings` `{ isActive, mode, hideTransactions }`; `GET /api/accounts?includeInactive=true`;
`GET /api/transactions?includeHidden=true`.
