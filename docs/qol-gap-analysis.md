# QoL gap analysis vs. Mint / Monarch / Simplifi / Credit Karma

Status after the gap-closing pass. "Have" = already existed; "Added" = built in this pass (with unit tests); "Blocked" = cannot be built without an external dependency.

| Feature | Competitors | FlowLedger |
|---|---|---|
| Account aggregation, net worth + history | all | Have |
| Auto-categorization, merchant aliases, tags | all | Have |
| Recurring/bill detection, bill calendar | Simplifi, Monarch | Have (Sheet, Recurring Flows) |
| Cash-flow forecast / overdraft warning | Simplifi | Have |
| Category budgets | all | Have |
| Custom reports / query | Monarch | Have (Query, Reports) |
| Savings goals | Monarch, Simplifi | **Added** — `/goals`, monthly amount needed per goal |
| Debt payoff planner (avalanche vs snowball) | Monarch, Credit Karma | **Added** — `/debt`, prefilled from liability accounts |
| Alerts (budget at 80% / over, goal past date) | all | **Added** — dashboard panel, `GET /api/alerts` |
| Rules engine (description contains X → category Y) | Monarch, Simplifi | **Added** — `/rules`, applied on save and after every sync; never overrides manual categories |
| Transaction notes | Monarch, Simplifi | **Added** — note icon in the Transactions grid |
| Split transactions across categories | Monarch, Simplifi | **Added** — split icon in the grid; Query/Budgets/Reports count each part in its own category |
| Investment performance (gain excluding deposits) | Monarch | **Added** — `/investments`, 30d / 90d / 1y per account |
| Credit score / credit report | Credit Karma, Mint | Blocked — needs a credit-bureau API partnership |

## Known limits

- Alerts are computed when the dashboard loads; there is no push/email delivery yet.
- Investment performance is a balance-snapshot calculation (no holdings or benchmark data), so its history starts when syncing started.
- The debt planner is stateless: APR and minimum payment are entered per run because providers do not supply them.
- Low-balance and bill-due alerts are not included yet.

## 2026-11 pass: gaps vs Monarch, YNAB, Rocket Money, Empower, Copilot

| Gap | Competitors | Status |
|---|---|---|
| Rate-aware savings/debt/refi suggestions, what-if simulators | Monarch, Empower (partial) | Have — `/strategies` (ADR 0015) |
| Duplicate detection and merge | Monarch | Have — `/duplicates` (ADR 0014) |
| Local, private AI categorization and coaching | Copilot (cloud) | Have — ADR 0013 |
| Retirement / financial-independence planner | Empower, Monarch | **Added** — FIRE tab on `/strategies`, `POST /api/strategies/simulate/fire` |
| Transactions CSV export (server side, filter-aware, formula-injection safe) | all | **Added** — `GET /api/transactions/export.csv` |
| Budget rollover (unspent carries forward) | YNAB, Monarch | Open — needs per-budget flag and schema change |
| Low-balance and bill-due alerts | Simplifi, Rocket Money | Open — needs forecast inputs in `GetAlertsHandler` |
| Email/push delivery of alerts | all | Open — only Home Assistant (ADR 0012) today |
| Subscription cancellation / bill negotiation | Rocket Money | Out of scope — needs a paid concierge service |
| Credit score | Credit Karma, Mint | Blocked — bureau partnership |
| Native mobile app | all | Open — web is responsive; no PWA manifest/offline yet |
| Partner/household shared views | Monarch, YNAB | Open — multi-user tenancy exists (ADR 0008), no shared household view |
