# PLAN.md

# FlowLedger

> A FOSS-first, self-hostable, cloud-native personal finance platform for forecasting, budgeting, asset tracking, transaction intelligence, and extensible money workflows. Built in .NET 10, Blazor, MudBlazor, Aspire, PostgreSQL, and container-native infrastructure.

## 0. Planning Status

This document is intended to be handed to a follow-on planning agent, then decomposed into subagent-led workflow loops.

Current stage: product and technical planning.

Primary goal: design a GitHub-ready repository that can be implemented incrementally, validated end to end, and later evolved into a SaaS without compromising the self-hosted FOSS experience.

## 1. Product Name

Recommended name: **FlowLedger**

Why:

- Communicates both cash movement and durable financial recordkeeping.
- Works for personal finance, forecasting, asset tracking, and future workflow automation.
- Avoids direct imitation of Mint, Simplifi, Quicken, YNAB, Actual, Firefly, or Maybe.
- Feels like a platform rather than a narrow budgeting app.

Working tagline:

> Know where your money went, where it is going, and when your plans become possible.

Repository name:

```text
flowledger
```

Primary package namespace:

```text
FlowLedger
```

Important legal note:

This name still needs trademark, domain, GitHub org, NuGet prefix, Docker image, and app-store availability checks before public launch.

## 2. Product Vision

FlowLedger combines four traditionally separate tools into one system:

1. A spreadsheet-like temporal money plan.
2. A Mint/Simplifi-style transaction and account aggregator.
3. An asset, debt, net-worth, and savings planner.
4. An extensible workflow platform for future personal finance automation.

The initial product should feel familiar to someone who already manages money in a spreadsheet. The difference is that FlowLedger can import account data, reconcile reality against the plan, forecast balances forward, explain deviations, and help the user plan savings goals, affordability dates, debt payoff, asset growth, and recurring money flows.

## 3. Core Product Principles

### 3.1 FOSS-first

The self-hosted version must be first-class, not a crippled community edition.

The default repository should be usable with:

```bash
./run.ps1
# or
./run.sh
```

Then available locally through Aspire and containers.

### 3.2 SaaS-ready without SaaS lock-in

The same codebase should support:

- Single-user self-hosting.
- Household/family self-hosting.
- Multi-tenant managed SaaS.
- Future white-label or managed private deployments.

The SaaS layer should add hosted convenience, billing, managed aggregation, managed backups, and operational support. It should not be required for basic ownership of personal data.

### 3.3 Spreadsheet-compatible UX

The spreadsheet view is not a secondary import/export feature. It is one of the core interaction models.

Users should be able to see:

- Every standard automated withdrawal.
- Every standard deposit.
- Every imported transaction.
- Every expected transfer.
- Every bill.
- Every recurring category.
- Every forecasted row.
- Paid, pending, expected, skipped, matched, and reconciled indicators.

### 3.4 Forecast-first

The system should answer questions like:

- How much money will be in checking on the 15th?
- Can I afford this purchase by August 1?
- When will I hit this savings target?
- What happens if this bill increases by $40?
- What if I move this payment by 5 days?
- What recurring outflows are increasing fastest?
- How accurate were my forecasts over the last 90 days?

### 3.5 Extensible core

The product should be designed as a financial workflow platform, not merely a budgeting CRUD app.

Future workflows should be addable without rewriting the core:

- Savings planners.
- Debt payoff strategies.
- Tax estimates.
- Subscription detection.
- Bill negotiation reminders.
- Shared household budgets.
- Receipt ingestion.
- Asset depreciation.
- Investment snapshots.
- Payroll forecasting.
- Alerting and automation rules.
- Financial scenario simulations.

## 4. Competitive Reference Points

FlowLedger should learn from, but not copy:

- Mint: automatic aggregation, categories, trends, simple dashboards.
- Quicken Simplifi: spending plan, recurring bills, subscriptions, projected balances.
- Actual Budget: FOSS, local-first feel, privacy posture.
- Firefly III: powerful self-hosted personal finance and double-entry strength.
- Maybe Finance: modern UI and net-worth dashboard direction.
- Spreadsheets: unmatched flexibility, auditability, and user confidence.

Differentiator:

> FlowLedger is the finance app for people whose spreadsheet already knows the future, but whose bank data knows what actually happened.

## 5. Target Users

### 5.1 Initial user

A technical self-hoster or spreadsheet power user who wants:

- Control over data.
- Forecasting beyond simple historical reports.
- A strong temporal view of money.
- Automated import with transparent reconciliation.
- A clean path from personal self-hosting to managed SaaS.

### 5.2 Secondary users

- Couples and households sharing financial planning.
- FIRE and savings-goal planners.
- People with irregular income.
- People recovering from debt who need precise cashflow forecasting.
- Small side-business operators who want a personal finance plus money-flow tool, but not full accounting software.

### 5.3 Non-goals for v1

FlowLedger is not initially:

- Full business accounting software.
- Tax filing software.
- A brokerage or trading platform.
- A crypto portfolio manager.
- A payments processor.
- A loan originator.
- A bank.

## 6. Product Surface

### 6.1 Dashboard

Purpose: quick financial state.

Cards:

- Current cash balance.
- Forecasted cash balance at next payday.
- Forecasted low-water mark before next payday.
- Net worth.
- Monthly planned income.
- Monthly planned outflows.
- Month-to-date actual spending.
- Forecast accuracy.
- Upcoming bills.
- Goal progress.
- Attention needed.

### 6.2 Spreadsheet Money Plan

The signature screen.

Rows may represent:

- Actual imported transaction.
- Planned recurring deposit.
- Planned recurring withdrawal.
- One-time planned event.
- Transfer.
- Debt payment.
- Savings contribution.
- Goal allocation.
- Forecast-only projection.
- Manual adjustment.

Columns:

- Date.
- Effective date.
- Posted date.
- Account.
- Merchant/payee/source.
- Description.
- Category.
- Flow type.
- Amount.
- Direction.
- Recurrence.
- Status.
- Paid indicator.
- Matched transaction.
- Confidence.
- Running account balance.
- Running household cash balance.
- Tags.
- Notes.
- Attachments.

Statuses:

- Planned.
- Pending.
- Posted.
- Matched.
- Reconciled.
- Skipped.
- Deferred.
- Ignored.
- Needs review.

Important behavior:

- Planned rows and actual rows can coexist.
- Actual transactions can match planned rows.
- Matching should preserve the plan and record the variance.
- Forecasts should be explainable row by row.

### 6.3 Accounts

Account types:

- Checking.
- Savings.
- Credit card.
- Loan.
- Mortgage.
- Investment.
- Cash.
- Manual asset.
- Manual liability.

Account properties:

- Institution.
- Provider connection.
- Current balance.
- Available balance.
- Credit limit.
- Interest rate.
- Statement cycle.
- Due date.
- Minimum payment.
- Currency.
- Sync state.
- Last successful aggregation.
- Last user-confirmed balance.

### 6.4 Transactions

Transaction sources:

- MX aggregation.
- CSV import.
- OFX/QFX import.
- Manual entry.
- Generated recurring plan.
- API import.
- Future plugin import.

Transaction capabilities:

- Categorization.
- Split transactions.
- Rule-based classification.
- Attachments.
- Notes.
- Tags.
- Merchant normalization.
- Duplicate detection.
- Reconciliation against planned rows.

### 6.5 Recurring Flows

Recurring flows are the backbone of forecasting.

Examples:

- Payroll deposit every other Friday.
- Mortgage on the 1st.
- Car payment on the 12th.
- Credit card autopay on statement due date.
- Utilities with estimated amount.
- Subscriptions.
- Childcare.
- Insurance.
- Transfers to savings.

Fields:

- Name.
- Account.
- Counterparty.
- Category.
- Amount model.
- Schedule model.
- Start date.
- End date.
- Grace window.
- Matching window.
- Forecast behavior.
- Variance handling.

Amount models:

- Fixed amount.
- Estimated amount.
- Last observed amount.
- Average of last N.
- Seasonal average.
- User formula.
- Statement balance.
- Minimum payment.
- Percentage of income.

Schedule models:

- Daily.
- Weekly.
- Every N weeks.
- Twice monthly.
- Monthly on day.
- Monthly on nth weekday.
- Last business day.
- Payday-derived.
- Custom RRULE-like schedule.

### 6.6 Forecasting

Forecast engine inputs:

- Current balances.
- Posted transactions.
- Pending transactions.
- Planned recurring flows.
- One-time planned events.
- Savings goals.
- Debt payoff plans.
- Account-specific rules.
- User-defined scenario overrides.

Forecast outputs:

- Account balance by date.
- Household cash balance by date.
- Low-water marks.
- Affordability dates.
- Goal completion dates.
- Debt payoff dates.
- Variance against prior forecast.
- Confidence and assumptions.

Forecast modes:

- Conservative.
- Expected.
- Optimistic.
- Custom scenario.

### 6.7 Budgeting

Support multiple budgeting models:

1. Spending plan budgeting.
2. Category budgeting.
3. Envelope-style budgeting.
4. Zero-based budgeting.
5. Forecast-based budgeting.

Initial v1 should focus on spending plan plus category budgets.

Budget periods:

- Weekly.
- Biweekly.
- Semimonthly.
- Monthly.
- Custom pay period.

### 6.8 Goals and Savings Planners

Goal types:

- Emergency fund.
- Purchase target.
- Trip.
- Annual bill.
- Debt payoff.
- Custom savings bucket.
- Home repair.
- School tuition.
- Vehicle replacement.

Planner behavior:

- Target amount.
- Target date.
- Starting balance.
- Linked account.
- Linked recurring contribution.
- Required contribution.
- Earliest affordable date.
- Scenario comparison.
- Risk warnings based on low-water cashflow.

### 6.9 Asset and Liability Tracking

Assets:

- Cash.
- Bank accounts.
- Investments.
- Vehicles.
- Home.
- Valuables.
- Business assets.
- Manual assets.

Liabilities:

- Credit cards.
- Loans.
- Mortgage.
- Medical debt.
- Student loans.
- Manual liabilities.

Net worth views:

- Current.
- Historical.
- Forecasted.
- By account type.
- By liquidity.
- By ownership.

### 6.10 Trends, Reports, and Graphs

Reports:

- Income vs spending.
- Category trends.
- Merchant trends.
- Recurring flow trends.
- Forecast accuracy.
- Net worth over time.
- Debt payoff timeline.
- Savings progress.
- Cashflow calendar.
- Subscription inventory.
- Spending anomalies.

Graph types:

- Line charts.
- Stacked category bars.
- Sankey-like money flow graph.
- Calendar heatmap.
- Running balance projection.
- Forecast variance bands.

### 6.11 Alerts and Rules

Rule engine examples:

- Alert if forecasted checking balance drops below $X.
- Alert if a bill has not matched within N days of expected date.
- Alert if a category exceeds budget by X percent.
- Alert if a merchant appears for the first time.
- Alert if subscription amount changes.
- Alert if a goal will miss its date.
- Auto-categorize merchant text matching pattern.
- Auto-tag transactions from specific account.

Rules should be stored as safe declarative definitions, not arbitrary code execution.

## 7. MX Integration Strategy

### 7.1 Objectives

Use MX to import financial accounts and transactions while minimizing API cost through TOS-approved methods.

General principles:

- Avoid aggressive polling.
- Cache provider data locally.
- Prefer incremental updates where supported.
- Use webhooks/event-driven refresh where available.
- Store normalized transactions and balances after retrieval.
- Let users manually request refreshes with guardrails.
- Keep sync logs transparent.
- Support manual and CSV fallback when aggregation is unavailable.

### 7.2 MX bounded context

Create a dedicated integration boundary:

```text
FlowLedger.Integrations.Mx
```

Responsibilities:

- MX client wrapper.
- Credential and token handling.
- Institution/member/account sync.
- Transaction import.
- Webhook handling.
- Provider error mapping.
- Sync cost accounting.
- Rate-limit protection.
- Connection repair flows.

Core domain should never directly depend on MX SDK/client types.

### 7.3 Sync lifecycle

States:

- Not connected.
- Connection pending.
- Connected.
- Syncing.
- Healthy.
- Degraded.
- Needs user action.
- Disabled.
- Error.

Sync strategy:

1. User links institution through MX Connect-style flow.
2. System records provider member reference.
3. Initial import retrieves account data and available transaction history.
4. System stores provider cursors, sync watermarks, and import fingerprints.
5. Future refreshes request only what is needed.
6. Webhooks enqueue background jobs.
7. Background jobs normalize, dedupe, match, categorize, and persist.
8. Forecast engine reacts to newly imported reality.

### 7.4 Cost controls

Controls:

- Per-tenant sync budget.
- Per-connection refresh cooldown.
- Per-provider exponential backoff.
- User-visible sync freshness.
- Scheduled background refresh windows.
- Webhook-first processing where possible.
- Manual refresh with warning when too frequent.
- Admin-level throttling in SaaS.
- Self-host override with explicit config.

### 7.5 Data retention

Store locally:

- Institution reference.
- Account reference.
- Normalized account snapshots.
- Normalized transactions.
- Transaction fingerprints.
- Sync status.
- Sync errors.
- Sync watermarks/cursors.
- Categorization metadata.

Do not store unless required:

- Raw credentials.
- Unneeded provider payloads.
- Sensitive tokens outside encrypted secret storage.

Raw provider payloads may be stored temporarily in a secure ingestion table for troubleshooting, but default retention should be short and configurable.

## 8. Technical Stack

### 8.1 Runtime

- .NET 10.
- C# 14.
- ASP.NET Core 10.
- Aspire 13.2 or newer, using latest compatible stable packages.
- Blazor Web App.
- MudBlazor.

### 8.2 Backend

- ASP.NET Core Minimal APIs or route groups by vertical slice.
- EF Core with PostgreSQL provider.
- Marten optional later for event/document projections, not v1 default.
- FluentValidation.
- MediatR optional, but prefer direct vertical slice handlers unless cross-cutting behavior justifies mediator.
- OpenTelemetry.
- Serilog.
- Health checks.
- ProblemDetails.
- Rate limiting.
- Idempotency middleware for write endpoints.

### 8.3 Frontend

- Blazor Web App with interactive server or auto mode initially.
- MudBlazor component library.
- CSS variables and theme system.
- Accessibility-first components.
- Spreadsheet/grid component abstraction.
- Charts through a replaceable chart adapter.

Candidate grid approaches:

- Start with MudBlazor DataGrid for normal tables.
- Introduce a dedicated virtualized spreadsheet component for the Money Plan screen.
- Keep spreadsheet engine separate from UI component.

### 8.4 Database

Primary database:

- PostgreSQL.

Optional local simple mode later:

- SQLite for single-user lightweight deployments, but not required for v1.

Supporting infrastructure:

- Redis for cache, distributed locks, and job coordination.
- Object storage for attachments and exports, S3-compatible where possible.
- Background jobs via Hangfire, Quartz.NET, or a custom hosted worker. Prefer Quartz.NET for scheduled finance tasks and predictable recurrence.

### 8.5 Deployment

Supported from day one:

- Docker Compose.
- Aspire local orchestration.
- GitHub Codespaces or Dev Container.

Supported by v1 release:

- Kubernetes manifests or Helm chart.
- Cloud-native deployment docs.
- Managed SaaS environment profile.

### 8.6 Package management

Use Central Package Management:

```text
Directory.Packages.props
```

Policy:

- Pin stable versions.
- Use latest compatible stable packages at project creation.
- Enable Dependabot or Renovate.
- No floating versions in main.
- Preview packages only behind explicit ADR approval.

## 9. Architecture

### 9.1 Style

Primary architecture:

- Modular monolith first.
- Vertical slice feature organization.
- DDD for core model and boundaries.
- Clean Architecture dependency direction.
- Event-driven internally.
- SaaS-ready tenancy from the beginning.

Reasoning:

A modular monolith is easier to build, test, self-host, and reason about than early microservices. Aspire still gives local orchestration for database, cache, workers, object storage, and future service splits.

### 9.2 Solution structure

```text
flowledger/
  PLAN.md
  README.md
  LICENSE
  SECURITY.md
  CONTRIBUTING.md
  CODE_OF_CONDUCT.md
  Directory.Build.props
  Directory.Packages.props
  global.json
  docker-compose.yml
  .editorconfig
  .gitignore
  .github/
    workflows/
      ci.yml
      e2e.yml
      docker.yml
      security.yml
    dependabot.yml
  eng/
    scripts/
      run.ps1
      run.sh
      test.ps1
      test.sh
      migrate.ps1
      seed.ps1
  docs/
    adr/
    architecture/
    deployment/
    user-guide/
    developer-guide/
  src/
    FlowLedger.AppHost/
    FlowLedger.ServiceDefaults/
    FlowLedger.Web/
    FlowLedger.Api/
    FlowLedger.Worker/
    FlowLedger.Domain/
    FlowLedger.Application/
    FlowLedger.Infrastructure/
    FlowLedger.Integrations.Mx/
    FlowLedger.Plugins.Abstractions/
    FlowLedger.SharedKernel/
  tests/
    FlowLedger.Domain.Tests/
    FlowLedger.Application.Tests/
    FlowLedger.Infrastructure.Tests/
    FlowLedger.Api.Tests/
    FlowLedger.Bdd.Tests/
    FlowLedger.E2E.Tests/
    FlowLedger.Architecture.Tests/
```

### 9.3 Bounded contexts

Initial bounded contexts:

1. Identity and Tenancy.
2. Accounts.
3. Transactions.
4. Money Plan.
5. Recurring Flows.
6. Forecasting.
7. Budgeting.
8. Goals.
9. Assets and Liabilities.
10. Reporting.
11. Imports and Integrations.
12. Rules and Alerts.
13. Audit and Compliance.

### 9.4 Dependency rules

Allowed:

```text
Web -> Application -> Domain
Api -> Application -> Domain
Worker -> Application -> Domain
Infrastructure -> Application abstractions + Domain
Integrations -> Application abstractions + Domain contracts
```

Not allowed:

```text
Domain -> EF Core
Domain -> ASP.NET Core
Domain -> MX
Domain -> MudBlazor
Application -> Web
Application -> Infrastructure concrete classes
```

Enforce with architecture tests.

### 9.5 Vertical slice layout

Example:

```text
src/FlowLedger.Application/Features/MoneyPlan/
  GetMoneyPlan/
    GetMoneyPlanQuery.cs
    GetMoneyPlanHandler.cs
    GetMoneyPlanValidator.cs
    GetMoneyPlanResponse.cs
  UpsertPlannedFlow/
    UpsertPlannedFlowCommand.cs
    UpsertPlannedFlowHandler.cs
    UpsertPlannedFlowValidator.cs
  ReconcileTransaction/
    ReconcileTransactionCommand.cs
    ReconcileTransactionHandler.cs
```

Each slice owns:

- Request model.
- Response model.
- Validation.
- Handler.
- Mapping.
- Authorization policy hook.
- Tests.

## 10. Domain Model

### 10.1 Core entities

```text
Tenant
Household
User
UserMembership
Account
Institution
ExternalConnection
AccountSnapshot
Transaction
TransactionSplit
Category
Merchant
RecurringFlow
PlannedFlowOccurrence
MoneyPlanRow
ForecastRun
ForecastPoint
Budget
BudgetPeriod
BudgetCategory
Goal
GoalContributionPlan
Asset
Liability
Rule
Alert
ImportBatch
AuditEvent
Attachment
```

### 10.2 Value objects

```text
Money
Currency
DateOnlyRange
RecurrencePattern
AccountId
TransactionId
TenantId
CategoryPath
MerchantFingerprint
TransactionFingerprint
ConfidenceScore
ForecastScenario
```

### 10.3 Domain events

```text
AccountConnected
AccountBalanceUpdated
TransactionImported
TransactionCategorized
RecurringFlowCreated
PlannedOccurrenceGenerated
TransactionMatchedToPlan
ForecastRequested
ForecastCompleted
BudgetExceeded
GoalProjectedCompletionChanged
LowBalancePredicted
SyncFailed
```

### 10.4 Transaction identity and dedupe

Transaction fingerprint inputs:

- Provider transaction id if available.
- Account id.
- Posted date.
- Amount.
- Normalized description.
- Merchant.
- Pending/posted state.

Deduping should support pending-to-posted transition without double counting.

### 10.5 Planned vs actual model

A planned row is a forecast assumption.
An actual transaction is observed reality.
A reconciliation link connects them.

Important records:

```text
PlannedFlowOccurrence
ActualTransaction
PlanActualMatch
Variance
```

Variance fields:

- Amount variance.
- Date variance.
- Category variance.
- Merchant variance.
- Confidence.
- Resolution state.

## 11. Forecasting Engine

### 11.1 Design

Create a deterministic forecasting engine in the domain/application layer.

Input:

```text
ForecastRequest
  TenantId
  Accounts
  StartingBalances
  PostedTransactions
  PendingTransactions
  PlannedOccurrences
  Goals
  ScenarioOverrides
  DateRange
  Mode
```

Output:

```text
ForecastResult
  ForecastRunId
  Points
  Rows
  LowWaterMarks
  GoalOutcomes
  Warnings
  Assumptions
```

### 11.2 Algorithm v1

1. Establish starting balance per account.
2. Build ordered ledger of dated events.
3. Include posted transactions through start date.
4. Include pending transactions when configured.
5. Generate recurring occurrences for forecast window.
6. Apply one-time planned events.
7. Apply transfers with source and destination symmetry.
8. Calculate running balances.
9. Identify low-water marks.
10. Calculate goal affordability and completion dates.
11. Emit explainable row-level assumptions.

### 11.3 Forecast confidence

Confidence inputs:

- Fixed recurring flow: high.
- Variable recurring flow with stable history: medium/high.
- Variable flow with high variance: low/medium.
- Manual future event: user-defined.
- Imported pending transaction: medium/high depending source.

### 11.4 Scenario planning

Scenario examples:

- Delay a bill.
- Add a purchase.
- Change income.
- Pause savings.
- Add debt payoff strategy.
- Simulate raise.
- Simulate emergency expense.

Scenarios should be immutable records so users can compare them.

## 12. Security and Privacy

### 12.1 Threat model

Sensitive data:

- Financial institutions.
- Account balances.
- Transactions.
- Income.
- Debt.
- Goals.
- Attachments.
- Provider references.
- User identity.

Threats:

- Account takeover.
- SaaS tenant data leakage.
- Provider token compromise.
- Unsafe plugin execution.
- Export leakage.
- Log leakage.
- SSRF through import URLs.
- Injection attacks.
- Broken access control.

### 12.2 Security requirements

- Tenant isolation on every query.
- Row-level tenant filters in EF Core.
- Authorization tests for every feature.
- No sensitive data in logs.
- Secrets stored in external secret stores or encrypted local storage.
- Support passkeys/OIDC for SaaS profile.
- Self-host local auth profile.
- MFA-ready identity model.
- Secure headers.
- CSRF protections for browser interactions.
- Rate limiting.
- Audit events for sensitive operations.
- Export confirmation and audit.
- Backup encryption docs.

### 12.3 Encryption

At minimum:

- TLS in production.
- Database at-rest encryption by deployment platform.
- Application-level encryption for provider tokens and other secrets.
- Optional field-level encryption for sensitive metadata.

### 12.4 Privacy posture

- Data export must be complete and user-owned.
- Data deletion must be documented and testable.
- SaaS telemetry must be opt-in for personal financial content.
- Self-hosted telemetry disabled by default.
- No sale of user financial data.

## 13. Multi-Tenancy

### 13.1 Tenancy model

Use tenant-aware architecture from day one.

Tenant types:

- Personal.
- Household.
- Organization later.

Tenant boundary:

- Accounts.
- Transactions.
- Categories.
- Goals.
- Forecasts.
- Rules.
- Connections.
- Attachments.

### 13.2 Implementation

Use a tenant context abstraction:

```csharp
public interface ITenantContext
{
    TenantId TenantId { get; }
    UserId UserId { get; }
}
```

Persistence must enforce tenant filters.

SaaS options:

- Shared database with tenant id for early SaaS.
- Optional database-per-tenant for enterprise/private managed instances later.

## 14. Extensibility

### 14.1 Plugin philosophy

Plugins should extend the platform safely.

Initial plugin surfaces:

- Import providers.
- Export providers.
- Categorization providers.
- Forecast scenario providers.
- Alert channels.
- Report widgets.

### 14.2 Plugin safety

For v1, avoid arbitrary runtime code loading in production.

Safer approach:

- Compile-time plugin packages.
- Declarative rules.
- Webhook/API integration points.
- Future WASM sandbox exploration.

### 14.3 Workflow engine

Initial workflow engine should be simple:

- Trigger.
- Condition.
- Action.

Example:

```text
When forecast low-water mark is below $250 before next payday,
if checking account is affected,
then create alert and suggest delaying optional planned expenses.
```

## 15. API Design

### 15.1 API style

- REST-ish resource APIs for application operations.
- Minimal APIs grouped by feature.
- ProblemDetails for errors.
- OpenAPI generated and committed in CI artifacts.
- Idempotency key support for critical writes.

### 15.2 Example endpoints

```text
GET    /api/accounts
POST   /api/accounts/manual
GET    /api/transactions
POST   /api/transactions/manual
POST   /api/imports/csv
GET    /api/money-plan
POST   /api/money-plan/planned-flows
POST   /api/money-plan/reconcile
POST   /api/forecasts/run
GET    /api/forecasts/{id}
GET    /api/budgets/current
POST   /api/goals
POST   /api/rules
POST   /api/integrations/mx/connect-token
POST   /api/integrations/mx/webhooks
```

## 16. UX Requirements

### 16.1 Design goals

- Fast and calm.
- Spreadsheet-friendly.
- Keyboard efficient.
- Clear visual state for planned vs actual.
- Strong empty states.
- Transparent assumptions.
- No mystery categorization.
- Dark mode and light mode.
- Custom categories, tags, and views.

### 16.2 Navigation

Main nav:

- Dashboard.
- Money Plan.
- Transactions.
- Accounts.
- Budget.
- Forecasts.
- Goals.
- Reports.
- Rules.
- Imports.
- Settings.

### 16.3 Money Plan UX

Must support:

- Virtualized rows.
- Sticky date/account/amount columns.
- Inline editing.
- Bulk editing.
- Filters.
- Grouping by account/category/month/pay period.
- Row status badges.
- Forecast line insertion.
- Match actual transaction to planned row.
- Split transaction from row.
- Keyboard shortcuts.
- CSV export.

### 16.4 Accessibility

- WCAG-aware contrast.
- Keyboard navigation.
- Screen reader labels.
- Semantic forms.
- Visible focus.
- Reduced motion option.

## 17. Testing Strategy

### 17.1 Test pyramid

Required layers:

1. Domain unit tests.
2. Application slice tests.
3. Infrastructure integration tests with Testcontainers.
4. API contract tests.
5. BDD tests with Reqnroll.
6. E2E tests with Playwright.
7. Architecture tests.
8. Security regression tests.
9. Performance smoke tests.

### 17.2 BDD examples

Feature: Forecast projected checking balance

```gherkin
Scenario: Forecast includes recurring payroll and bills
  Given I have a checking account with a current balance of $1,000
  And I receive payroll of $2,000 every other Friday
  And I pay rent of $1,500 on the 1st of each month
  When I forecast through the next 45 days
  Then I should see the projected balance for each dated event
  And the forecast should identify the lowest projected balance
```

Feature: Match actual transaction to planned bill

```gherkin
Scenario: Imported utility payment matches planned recurring flow
  Given I planned a utility bill of $180 on June 10
  When a posted transaction for $184.25 from the utility company imports on June 11
  Then the transaction should be matched to the planned bill
  And the variance should be $4.25
  And the planned row should be marked as matched
```

### 17.3 E2E flows

Playwright must cover:

- First-run setup.
- Create manual account.
- Add recurring payroll.
- Add recurring bill.
- View money plan.
- Run forecast.
- Import CSV.
- Match transaction.
- Create goal.
- View dashboard.
- Dark mode smoke.

### 17.4 Coverage policy

Target:

- Domain: 95 percent meaningful branch coverage.
- Application: 90 percent meaningful branch coverage.
- API: all endpoints covered by integration or contract tests.
- E2E: critical paths only, not exhaustive permutations.

Coverage is a quality signal, not a substitute for behavior-focused tests.

## 18. Documentation

Required docs:

```text
README.md
SECURITY.md
CONTRIBUTING.md
docs/getting-started.md
docs/self-hosting/docker-compose.md
docs/self-hosting/kubernetes.md
docs/development/local-dev.md
docs/development/testing.md
docs/architecture/overview.md
docs/architecture/bounded-contexts.md
docs/architecture/forecasting.md
docs/architecture/mx-integration.md
docs/user-guide/money-plan.md
docs/user-guide/forecasting.md
docs/user-guide/imports.md
docs/user-guide/goals.md
docs/adr/
```

ADR seed list:

- ADR-0001: Modular monolith first.
- ADR-0002: PostgreSQL as primary database.
- ADR-0003: Aspire for local orchestration.
- ADR-0004: Blazor plus MudBlazor for UI.
- ADR-0005: Planned rows and actual transactions remain separate.
- ADR-0006: MX isolated behind integration boundary.
- ADR-0007: Tenant-aware design from day one.
- ADR-0008: No arbitrary runtime plugin execution in v1.

## 19. GitHub Readiness

### 19.1 Required repo files

- README.md.
- PLAN.md.
- LICENSE.
- SECURITY.md.
- CONTRIBUTING.md.
- CODE_OF_CONDUCT.md.
- .editorconfig.
- .gitignore.
- global.json.
- Directory.Build.props.
- Directory.Packages.props.
- docker-compose.yml.
- devcontainer config.
- GitHub Actions workflows.

### 19.2 CI workflows

ci.yml:

- Restore.
- Format check.
- Build.
- Unit tests.
- Application tests.
- Architecture tests.
- Integration tests.
- Publish test results.
- Upload coverage.

security.yml:

- Dependency vulnerability scan.
- Secret scanning guidance.
- CodeQL.
- Container scan.

integration.yml:

- PostgreSQL Testcontainers.
- Redis Testcontainers.
- Migration validation.

E2E.yml:

- Start Aspire or compose profile.
- Run Playwright tests.
- Upload traces, videos, and screenshots on failure.

Docker.yml:

- Build images.
- SBOM.
- Image scan.
- Push only on tagged release.

### 19.3 Quality gates

A PR is not mergeable unless:

- `dotnet format --verify-no-changes` passes.
- Build passes with warnings as errors for product code.
- Tests pass.
- Migrations are valid.
- Architecture tests pass.
- No high/critical dependency vulnerabilities without explicit exception.
- Public APIs are documented or intentionally internal.

## 20. One-Script Run

### 20.1 Developer command

PowerShell:

```powershell
./eng/scripts/run.ps1
```

Bash:

```bash
./eng/scripts/run.sh
```

Behavior:

1. Validate .NET SDK.
2. Validate Aspire CLI.
3. Validate container runtime.
4. Restore tools.
5. Restore packages.
6. Start dependencies.
7. Apply migrations.
8. Seed demo data.
9. Start AppHost.
10. Print URLs.

### 20.2 Test command

```bash
./eng/scripts/test.sh
```

Behavior:

- Run format check.
- Build.
- Run unit tests.
- Run integration tests.
- Run BDD tests.
- Optionally run E2E tests.

## 21. Data Import and Export

### 21.1 Import formats

V1:

- CSV.
- Manual entry.
- MX aggregation.

V1.1:

- OFX.
- QFX.
- QIF.

### 21.2 Export formats

- CSV.
- JSON backup.
- Full tenant export.
- Forecast export.
- Money plan export.

### 21.3 Backup and restore

Self-hosted users must have:

- Documented database backup.
- Attachment backup.
- Config backup.
- Restore validation procedure.

SaaS users must have:

- Full data export.
- Account deletion.
- Tenant deletion.

## 22. Observability

### 22.1 Local

Use Aspire dashboard for:

- Logs.
- Metrics.
- Traces.
- Health.
- Resource status.

### 22.2 Production

Use OpenTelemetry-compatible exporters.

Track:

- API latency.
- Forecast runtime.
- Sync job duration.
- Sync errors by provider.
- Import volume.
- Background job failures.
- Database query latency.
- E2E synthetic health.

Never track sensitive transaction descriptions, amounts, account numbers, or raw provider payloads in telemetry.

## 23. Performance Requirements

Initial targets:

- Dashboard loads under 1 second locally with seeded data.
- Money Plan supports 10,000 rows with virtualization.
- Forecast for 10 accounts and 3 years completes under 500 ms in domain tests.
- CSV import of 10,000 rows completes under 10 seconds locally.
- API p95 under 250 ms for common reads on normal data volume.

Performance tests should use generated realistic data.

## 24. Seed Data

Seed demo household:

- Checking.
- Savings.
- Credit card.
- Mortgage or rent.
- Payroll.
- Utilities.
- Subscriptions.
- Groceries.
- Fuel.
- Insurance.
- Emergency fund goal.
- Vacation goal.
- Debt payoff goal.

Include enough history to make charts and trends useful.

## 25. Licensing

Recommended license for FOSS-first SaaS-sensitive project:

- AGPL-3.0 if the goal is to require hosted modifications to remain open.
- Apache-2.0 or MIT if the goal is maximum adoption and commercial friendliness.

Recommended initial choice:

```text
AGPL-3.0-only
```

Rationale:

The product handles sensitive personal financial data and is likely to be offered as SaaS. AGPL protects the open hosted commons better than permissive licenses.

Open decision:

Consider dual licensing later for commercial embedding or white-label deals.

## 26. MVP Scope

### 26.1 MVP must include

- Single-user or single-household mode.
- Local auth.
- Manual accounts.
- Manual transactions.
- CSV import.
- Categories.
- Recurring flows.
- Spreadsheet Money Plan.
- Deterministic forecast engine.
- Dashboard.
- Goals.
- Basic reports.
- Docker Compose.
- Aspire AppHost.
- GitHub Actions CI.
- Tests across domain, application, BDD, and E2E.

### 26.2 MVP should include

- MX integration scaffold behind feature flag.
- Provider abstraction.
- Sync job architecture.
- Basic transaction matching.
- Rule-based categorization.
- Full tenant export.

### 26.3 MVP should not include

- Full SaaS billing.
- Mobile apps.
- Investment deep analysis.
- Tax optimization.
- Arbitrary plugin loading.
- AI financial advice.
- Payment movement.

## 27. Milestones

### Milestone 0: Repo foundation

Deliverables:

- Solution scaffold.
- Aspire AppHost.
- Web app shell.
- API shell.
- Worker shell.
- PostgreSQL and Redis resources.
- Central package management.
- Build/test scripts.
- CI pipeline.
- Basic docs.

Acceptance:

- One-script run works.
- CI passes.
- App shell displays.
- Health checks pass.

### Milestone 1: Core domain

Deliverables:

- Money value objects.
- Account model.
- Transaction model.
- Category model.
- Recurring flow model.
- Planned occurrence model.
- Domain events.
- Unit tests.

Acceptance:

- Domain tests cover core invariants.
- Architecture tests enforce dependency rules.

### Milestone 2: Persistence and APIs

Deliverables:

- EF Core mappings.
- Migrations.
- Account APIs.
- Transaction APIs.
- Category APIs.
- Recurring flow APIs.
- Integration tests.

Acceptance:

- CRUD flows work through API.
- Tenant filters tested.
- Migrations apply from empty database.

### Milestone 3: Money Plan

Deliverables:

- Money Plan projection.
- Spreadsheet view.
- Inline planned row editing.
- Row statuses.
- Running balance.
- Filters.

Acceptance:

- User can create spreadsheet-like future plan.
- Running balances are correct.
- BDD tests pass.

### Milestone 4: Forecasting

Deliverables:

- Forecast engine.
- Forecast API.
- Forecast UI.
- Low-water mark detection.
- Goal affordability calculation.

Acceptance:

- Forecast explains each balance change.
- Scenario tests pass.

### Milestone 5: Imports and matching

Deliverables:

- CSV import.
- Import review UI.
- Duplicate detection.
- Planned vs actual matching.
- Categorization rules v1.

Acceptance:

- User imports transactions and reconciles against plan.
- Variance is visible.

### Milestone 6: Goals and reports

Deliverables:

- Savings goals.
- Goal contribution planner.
- Trends dashboard.
- Category report.
- Net-worth report.

Acceptance:

- User can forecast when a goal becomes affordable.
- Reports match seeded data expectations.

### Milestone 7: MX integration beta

Deliverables:

- MX connection feature flag.
- MX client abstraction.
- Connection flow.
- Account import.
- Transaction import.
- Webhook endpoint.
- Sync job queue.
- Cost controls.

Acceptance:

- Sandbox integration works.
- Sync avoids unnecessary repeated imports.
- Provider errors are visible and actionable.

### Milestone 8: Self-hosted release candidate

Deliverables:

- Docker Compose release profile.
- Backup/restore docs.
- Security docs.
- Full E2E smoke.
- Demo data.
- Release artifacts.

Acceptance:

- Fresh clone can run locally.
- Fresh server can deploy with documented steps.
- Critical flows pass.

### Milestone 9: SaaS foundation

Deliverables:

- Tenant management.
- Invite users.
- Hosted auth profile.
- Admin console.
- Billing placeholder abstraction.
- SaaS observability profile.

Acceptance:

- Multiple tenants isolated.
- Admin cannot accidentally cross tenant boundaries through normal app paths.

## 28. Subagent Workflow Breakdown

The next planning agent should split implementation into specialized loops.

### 28.1 Product agent

Owns:

- User stories.
- Acceptance criteria.
- UX flows.
- Terminology.
- MVP scope guardrails.

First tasks:

- Convert this plan into epics and user stories.
- Define Money Plan wireframes.
- Define first-run onboarding.

### 28.2 Architecture agent

Owns:

- Solution structure.
- ADRs.
- Dependency rules.
- Modular boundaries.
- Package strategy.

First tasks:

- Create ADRs 0001 through 0008.
- Define project references.
- Define architecture test rules.

### 28.3 Domain agent

Owns:

- DDD model.
- Value objects.
- Domain events.
- Invariants.
- Forecast core primitives.

First tasks:

- Implement Money, Currency, Account, Transaction, RecurringFlow.
- Write domain tests before application code.

### 28.4 Persistence agent

Owns:

- EF Core mappings.
- PostgreSQL schema.
- Migrations.
- Query performance.
- Tenant filters.

First tasks:

- Model database schema.
- Implement migrations.
- Add Testcontainers integration tests.

### 28.5 API agent

Owns:

- Minimal API route groups.
- Request/response contracts.
- Validation.
- ProblemDetails.
- OpenAPI.

First tasks:

- Build account, transaction, category, recurring flow endpoints.
- Add contract tests.

### 28.6 UI agent

Owns:

- Blazor layout.
- MudBlazor theme.
- Money Plan screen.
- Dashboard.
- Forms and validation.
- Accessibility.

First tasks:

- App shell.
- Navigation.
- Dashboard mock.
- Money Plan virtualized grid prototype.

### 28.7 Forecasting agent

Owns:

- Forecast algorithm.
- Scenario model.
- Low-water marks.
- Goal affordability.
- Forecast explainability.

First tasks:

- Implement deterministic forecast engine with pure tests.
- Add benchmark/performance smoke.

### 28.8 Integration agent

Owns:

- MX abstraction.
- CSV import.
- Provider sync lifecycle.
- Webhooks.
- Cost controls.

First tasks:

- Implement CSV import first.
- Design MX contracts behind feature flag.
- Add provider simulator for tests.

### 28.9 QA agent

Owns:

- Reqnroll specs.
- Playwright E2E.
- Test data.
- Coverage thresholds.
- Regression suite.

First tasks:

- Create BDD project.
- Add first forecast feature.
- Add first onboarding E2E.

### 28.10 DevOps agent

Owns:

- Aspire.
- Docker Compose.
- GitHub Actions.
- Dev Container.
- Release images.
- SBOM and scans.

First tasks:

- AppHost resource graph.
- One-script run.
- CI workflow.
- Dockerfile.

### 28.11 Security agent

Owns:

- Threat model.
- Authn/authz.
- Tenant isolation.
- Secrets.
- Logging hygiene.
- Security docs.

First tasks:

- Create SECURITY.md.
- Define sensitive logging rules.
- Add authorization tests.

## 29. Initial Backlog

### Epic: Repo foundation

- Create solution and project structure.
- Add global.json targeting .NET 10 SDK.
- Add Directory.Packages.props.
- Add Aspire AppHost.
- Add ServiceDefaults.
- Add Web shell.
- Add API shell.
- Add Worker shell.
- Add PostgreSQL resource.
- Add Redis resource.
- Add one-script run.
- Add CI.

### Epic: Financial core

- Implement Money value object.
- Implement Currency value object.
- Implement Account aggregate.
- Implement Transaction aggregate.
- Implement Category model.
- Implement RecurringFlow aggregate.
- Implement recurrence expansion.
- Implement planned occurrence generation.

### Epic: Money Plan

- Create MoneyPlanRow projection.
- Create MoneyPlan API.
- Create spreadsheet grid.
- Add running balance calculation.
- Add row status model.
- Add planned/actual matching.

### Epic: Forecasting

- Create ForecastRequest.
- Create ForecastResult.
- Implement event ordering.
- Implement account running balances.
- Implement low-water mark detection.
- Implement goal affordability.
- Add scenario overrides.

### Epic: Importing

- CSV upload.
- CSV mapping UI.
- Import batch model.
- Duplicate detection.
- Transaction normalization.
- Import review.

### Epic: MX beta

- MX config.
- MX client wrapper.
- Connect token endpoint.
- Webhook endpoint.
- Account sync job.
- Transaction sync job.
- Sync status UI.
- Cost controls.

### Epic: QA

- Reqnroll setup.
- Playwright setup.
- Testcontainers setup.
- Architecture tests.
- Coverage collection.
- Seed data.

## 30. Risks and Mitigations

### Risk: MX costs become hard to control

Mitigation:

- Feature flag MX.
- Build CSV/manual flows first.
- Implement sync budgets and cooldowns.
- Prefer event-driven sync.
- Persist normalized data locally.

### Risk: Spreadsheet UX becomes too complex

Mitigation:

- Build projection engine separately from visual grid.
- Start with read-heavy grid.
- Add editing incrementally.
- Keep keyboard shortcuts scoped.

### Risk: Forecasts lose user trust

Mitigation:

- Make every forecast row explainable.
- Show assumptions.
- Track forecast accuracy.
- Preserve planned vs actual variance.

### Risk: SaaS needs distort self-hosted product

Mitigation:

- Self-hosted release is a first-class milestone.
- SaaS uses the same core.
- No hosted-only core budgeting features.

### Risk: Over-engineering delays MVP

Mitigation:

- Modular monolith.
- Vertical slices.
- Avoid plugin runtime in v1.
- Build manual and CSV flows before MX.

## 31. Definition of Done

A feature is done when:

- User story acceptance criteria pass.
- Domain invariants are tested.
- Application behavior is tested.
- API validation and errors are tested.
- UI happy path is covered when applicable.
- BDD spec exists for core financial behavior.
- E2E coverage exists for critical user path when applicable.
- Docs are updated.
- Observability is considered.
- Authorization is enforced.
- Tenant isolation is tested.
- No sensitive data is logged.

## 32. Agent Handoff Prompt

Use this prompt for the next planning agent:

```text
You are the implementation planning agent for FlowLedger, a FOSS-first self-hostable .NET 10 personal finance platform. Read PLAN.md completely. Convert it into an implementation program with epics, vertical slices, subagent assignments, acceptance criteria, and dependency order. Preserve the architectural constraints: .NET 10, C# 14, Aspire 13.2+, Blazor, MudBlazor, PostgreSQL, modular monolith, vertical slices, DDD, Reqnroll BDD, Playwright E2E, GitHub Actions, one-script run, self-hosted first, SaaS-ready, MX isolated behind an integration boundary, planned rows separate from actual transactions, deterministic explainable forecasting. Output the next-level implementation plan without changing the product vision unless a conflict or risk requires an ADR.
```

## 33. Immediate Next Actions

1. Validate the FlowLedger name.
2. Choose license.
3. Create repo scaffold.
4. Create ADRs.
5. Build one-script run.
6. Implement domain primitives.
7. Build Money Plan read model.
8. Implement deterministic forecast engine.
9. Add CSV import.
10. Add MX beta behind feature flag.

