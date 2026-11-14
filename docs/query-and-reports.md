# Query and reports

FlowLedger has a report builder: write a pipe-style query over your own data, pick a chart, and save the
result as a **view** (one query) or add it to a **report** (several panels). Pages: `/query` (workbench) and
`/reports` (saved items and presets).

## Language

Kusto/Splunk-flavoured pipelines, read left to right:

```
transactions
| where direction == "Debit" and date >= ago(90d)
| summarize total = sum(amount), n = count() by category
| sort by total desc
| take 12
```

| Operator | Purpose |
|---|---|
| `where cond` | Filter rows (`and`, `or`, `not`, `== != < <= > >=`, `contains`, `startswith`, `endswith`, `in (…)`, `between(a, b)`) |
| `project a, b = expr` | Choose / compute columns |
| `extend name = expr` | Add or replace a column |
| `summarize agg… by key…` | Group; output is ordered by the keys ascending, so trends read chronologically |
| `sort by x [asc\|desc]` | Order (descending by default) |
| `top N by x` / `take N` | Limit rows |
| `distinct a, b` / `count` | Unique rows / row count |

Aggregates: `count()`, `sum`, `avg`, `min`, `max`, `dcount`, `countif(cond)`, `sumif(x, cond)`.
Scalar functions: `ago(30d)`, `today()`, `month(d)` (`2026-03`), `quarter`, `year`, `week`, `startofmonth`,
`dayofweek`, `datediff`, `abs`, `round`, `floor`, `ceil`, `lower`, `upper`, `len`, `strcat`, `coalesce`, `iif`.
Durations: `30d`, `2w`, `6mo`, `1y`. Dates compare with `"2026-01-01"` strings. `//` starts a comment.

Datasets: `transactions` (posted + pending; planned forecast rows excluded), `accounts`, `assets`.
`GET /api/query/schema` lists every column and function.

## Visualisations

Table, column, bar, line (multi-trend), area (optionally stacked) and donut. A **series column** pivots
long-form rows into one line/bar per value (e.g. `by month, category`). More than eight series fold into
*Other*. Every chart has a data-table view.

## Saving

- A **view** has exactly one panel; a **report** has 1–12. Both are stored per tenant (`saved_reports`).
- Built-in presets (`preset:<slug>`) are read-only; "Save a copy" makes an editable one.
- Reports run all panels server-side (`GET /api/reports/{id}/run`); a failing panel reports its error
  without failing the report.

## Safety

Queries never touch SQL. They run in memory over datasets already filtered to the caller's tenant (at most
50,000 transactions per run), results are capped at 5,000 rows, and queries at 4,000 characters.

## API

`GET /api/query/schema`, `POST /api/query/run`, `GET|POST /api/reports`, `GET /api/reports/{id}`,
`GET /api/reports/{id}/run`, `PUT|DELETE /api/reports/{guid}`.
