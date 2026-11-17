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
