# FlowLedger Advanced User Guide

This guide covers the pages and workflows that go beyond the basics. For the Dashboard,
Accounts, Transactions, Money Plan, Recurring Flows and Forecasts, see the
[User Guide](user-guide.md). New here? Start with [Onboarding](onboarding.md).

Behavior below is taken from the application source. Where the UI offers no way to do something, the text says so.

---

## Table of Contents

1. [Spending (drill-down)](#spending-drill-down)
2. [Categories (rename, move, merge)](#categories-rename-move-merge)
3. [How a transaction gets its category (precedence)](#how-a-transaction-gets-its-category-precedence)
4. [Rules](#rules)
5. [Recategorizing a transaction](#recategorizing-a-transaction)
6. [Transaction notes and splits](#transaction-notes-and-splits)
7. [Budgets](#budgets)
8. [Goals](#goals)
9. [Alerts](#alerts)
10. [Suspicious activity and dismissing with a note](#suspicious-activity-and-dismissing-with-a-note)
11. [Debt payoff planner](#debt-payoff-planner)
12. [Investments](#investments)
13. [Assets](#assets)
14. [Query and Ask (natural language)](#query-and-ask-natural-language)
15. [Reports](#reports)
16. [Bill Sheet](#bill-sheet)
17. [Data and Backups (owner only)](#data-and-backups-owner-only)
18. [Linked logins](#linked-logins)

---

## Spending (drill-down)

**Page:** Spending (`/spending`). Where your money went in a period, as a donut chart,
a trend bar chart and a list of the biggest transactions.

**How to use it**

1. Pick a **Period** (This month, Last month, Last 30 days, Last 90 days, Year to date,
   Last 12 months, All time, or Custom with From/To dates). Optionally narrow to one or
   more **Accounts**.
2. Switch between **Spending** and **Income**, and choose how to group: **Category**,
   **Tag**, **Merchant** or **Account**.
3. In Category mode, click a slice (or a row with a chevron) to drill into its
   sub-categories. The breadcrumb at the top (`All categories > Food > ...`) takes you
   back up.
4. Click a slice that has no children, or the filter icon on a row, to filter the trend
   chart and transaction list to just that slice. Click the "Filtered" chip to clear it.
5. In the transaction list, click a category chip to [recategorize](#recategorizing-a-transaction).
   If a grey `ML nn%` chip or a blue `Suggested: ...` chip appears, see the gotchas.

**Gotchas**

- Transfers between your own accounts are always excluded. The **Include transfers &
  investments** switch also brings back investment and other non-spending categories.
- The card "vs previous period" compares with the equally long period just before.
- The trend buckets are chosen automatically: daily up to 45 days, weekly up to 120 days,
  monthly beyond that.
- The transaction list shows at most the 200 largest, largest first.
- The donut shows the top 9 groups and folds the rest into "Other" (not clickable).
- `ML nn%` means the learned model categorized it automatically. A `Suggested: ...` chip
  is a one-click proposal that has **not** been applied until you click it. By default
  the model only suggests; it does not auto-apply.
- Changing any filter resets the drill-down and selection.

---

## Categories (rename, move, merge)

**Page:** Categories (`/categories`). A tree of every category. Categories are paths like
`Food/Dining`; the part before the `/` is the parent.

**How to use it**

1. Use the search box to find a category (matches and their parents are shown), or the
   expand/collapse-all buttons.
2. Click the rename icon on a row. The dialog is titled **Rename, move or merge**.
3. Type a new path in **New path**.
   - A path that does not exist yet renames or moves the category (and everything under it).
   - An existing category from the dropdown merges into it.
4. Review the live preview. Merges are shown with a merge icon, moves with an arrow. Up to
   six changes are listed, then "+ N more".
5. Click the action button (labelled **Rename**, **Move N** or **Merge N** depending on the
   preview). The result toast reports how many categories moved or merged and how many
   transactions changed.

**Gotchas**

- Transactions, splits, recurring flows, rules and budgets all follow the change.
- **Merges cannot be undone.** Merged categories cannot be split apart again. Moves and
  renames can be reversed by moving back.
- Categories marked **system** (for example Transfer) cannot be renamed or moved, and a
  parent containing one cannot be moved either.
- You cannot move a category inside itself, and the new path must differ from the old one.
- A parent that exists only because a child exists (for example `Food` when only
  `Food/Dining` has transactions) can still be renamed or moved.
- Categories only show up in the list once something uses them; new ones are created when
  you recategorize or save a rule.
- Paths are limited to 500 characters (rule and recategorize paths to 200).

---

## How a transaction gets its category (precedence)

Several things can set a category. In order of strength:

1. **You, by hand.** A category you set on a transaction is never overridden by automation
   or by rules. (The one exception: "apply to all from this merchant" in
   [Recategorize](#recategorizing-a-transaction) deliberately overwrites older manual
   choices for that merchant.)
2. **Rules** ([Rules page](#rules)). Fill uncategorized transactions and replace
   *automatic* categories. The longest matching text wins.
3. **Auto-categorize** (button on Transactions). Only fills transactions that have **no**
   category. In order: own-account transfer, then the category you most often gave that
   merchant, then built-in keyword rules, then the learned model (suggestions by default).
   It never changes a transaction that already has a category.

Because auto-categorize skips anything already categorized, fix a wrong automatic category
with a rule or a recategorize, not by re-running auto-categorize.

---

## Rules

**Page:** Rules (`/rules`). "If the description contains X, use category Y", applied now and
on every future sync.

**How to use it**

1. Enter the text under **If description contains** (a short, distinctive fragment of the
   bank description, for example `starbucks`).
2. Enter the **Category**, for example `Food/Coffee`. It is created if it does not exist.
3. Click **Save rule**. The toast shows how many transactions were updated immediately.
4. To remove a rule, click the bin icon beside it. Removing a rule does not undo
   categories it already applied.

**Gotchas**

- Matching ignores case, spaces and punctuation, and looks at both description and merchant
  name.
- If several rules match, the **longest** pattern wins, so `amazon prime` beats `amazon`.
- Rules never override a category you set by hand on a transaction.
- Saving a rule with an existing text replaces that rule's category.
- Recategorizing with "all from this merchant" also saves a rule automatically, so check
  here to see what has been created for you.
- To rename how a merchant is *displayed* (not its category), use the pencil icon beside the
  merchant on the Transactions page ("Rename merchant"); that is a separate alias feature.

---

## Recategorizing a transaction

Available by clicking a category chip on the Spending page transaction list (a dialog titled
**Change category**), and via the category dropdown on the Transactions page.

**How to use it (Spending page dialog)**

1. Click the category chip on the transaction.
2. Search for a category or type a new path (for example `Food/Takeout`). A new path shows
   "Create ...".
3. Choose scope: **Just this transaction**, or **All transactions from "merchant"**.
4. Optionally add a note.
5. Click **Move transaction** or **Move all matching**.

**Gotchas**

- "All" changes every transaction of that merchant, past and future, including ones you
  categorized by hand earlier, and saves a rule so future syncs follow.
- For a single transaction a toast offers **Undo**. There is no undo toast for the "all"
  option.
- A note typed in this dialog is **appended** to any existing note on the transaction.
- On the Transactions page the dropdown has no scope dialog; instead the checkbox **Apply
  category changes to same merchant** (on by default) decides whether the change spreads to
  the merchant's other transactions. Untick it to change only one. This path is gentler than
  the Spending dialog's "all" option: it only relabels the merchant's transactions that are
  uncategorized or were categorized automatically, leaves any you set by hand, and does
  **not** save a rule.

---

## Transaction notes and splits

On the Transactions page, three icons sit beside the merchant name.

**Notes**

1. Click the note icon (outlined when empty, filled when a note exists; hover to read it).
2. Type up to 1,000 characters and click **Save**. Saving a blank note removes it.
3. Notes are searchable from the Transactions search box.

**Rename merchant**: the pencil icon lets you set a display name for every transaction whose
bank description contains the text. The original description is kept.

**Splits** (one purchase, several categories)

1. Click the split icon.
2. Enter an amount and category for each part. Two parts are offered; add more with **Add
   part** (at least two parts are always kept).
3. The parts must add up exactly to the transaction total; the dialog shows what is left to
   assign. Each amount must be above zero.
4. Click **Save**. To undo, open the dialog again and click **Remove split**.

**Gotchas**

- Reports and budgets count each part in its own category.
- The note dialog **replaces** the note, while recategorize and suspicious-activity
  dismissal **append** to it (a new line).
- Notes added by dismissing a flag also show up here.
- **Export CSV** exports the visible rows with their single category; it does not contain
  notes or split parts. Use the Query/Ask page or a saved report if you need split parts.

---

## Budgets

**Page:** Budgets (`/budgets`). Monthly limits per category, tracked against this calendar
month.

**How to use it**

1. Pick a **Category** (the list shows what you have spent in it this month) and enter a
   **Monthly limit**, then click **Save budget**. Saving again for the same category
   changes the limit.
2. Or click one of the **Suggestions** chips (top spending categories without a budget).
   It pre-fills a rounded limit slightly above what you have spent so far; adjust and save.
3. Watch the bars: green is fine, amber at 80% or more, red when over. Remove a budget with
   the bin icon.

**Gotchas**

- A budget on a parent (for example `Food`) covers all its sub-categories.
- Transfers between your own accounts never count. Only debits count.
- Splits count in their own categories.
- Uncategorized spending cannot be budgeted; categorize it first.
- Budgets are monthly and based on the current month only. There is no rollover or per-month
  history on this page.
- At 80% used you get a warning [alert](#alerts) on the Dashboard; over the limit is an error.

---

## Goals

**Page:** Goals (`/goals`). Savings targets (emergency fund, vacation, down payment).

**How to use it**

1. Enter a **Name**, a **Target**, how much you have **Saved so far**, and optionally a
   **Target date**. Click **Save goal**.
2. With a target date, the goal shows **monthly needed** to reach the target by then.
3. Update progress by clicking the pencil icon, changing **Saved so far** and saving.
4. Remove with the bin icon.

**Gotchas**

- The saved amount is something you type. Goals are **not** linked to an account, so they do
  not update from synced balances. Keep it current by hand.
- A goal past its target date and not yet complete raises a warning [alert](#alerts).
- A goal needs a target above zero to save.

---

## Alerts

Alerts appear in a panel at the top of the Dashboard. They are computed live from your data,
so there is nothing to dismiss; they disappear when the condition clears.

| Alert | Level | When |
|-------|-------|------|
| "<Category> is over budget" | Error (red) | Spent this month exceeds the limit |
| "<Category> budget is N% used" | Warning | 80% or more of the limit is used |
| "Goal <name> is past its target date" | Warning | Target date passed and goal not complete |

Errors are listed first. The header also shows a red backup warning chip (linking to Data &
Backups) in production when the last backup is stale or no off-site copy is configured.

Separately, the [Suspicious Activity](#suspicious-activity-and-dismissing-with-a-note) card on
the Dashboard flags individual transactions, and the Dashboard shows overdraft risk (see the
[User Guide](user-guide.md#dashboard)).

---

## Suspicious activity and dismissing with a note

**Where:** the Dashboard "Needs a Look" card and the **Suspicious Activity** card beneath it.
These are heuristics: worth a second look, not proof of fraud.

A debit in the last 30 days can be flagged for reasons such as:

- Possible duplicate charge (same account, merchant and amount within three days).
- A charge several times the usual amount for that merchant.
- A large charge from a merchant not seen before.
- A subscription price increase on a steady charge.
- An unusual amount for this merchant, or unusually large for its category (needs enough
  history, roughly five charges per merchant or ten per category).

Transfers between your own accounts, investments and card payments are never flagged.

**How to dismiss**

1. On the card, click **Dismiss** beside the transaction.
2. In the **Dismiss alert** dialog, optionally type a note (up to 500 characters), for
   example "Verified with the merchant, expected charge".
3. Click **Dismiss**. The transaction stops appearing under Suspicious Activity.

**Gotchas**

- A dismissal is per transaction. The same merchant can be flagged again on a later
  transaction.
- If you typed a note it is also appended to the transaction's own notes, so it shows the
  note icon on Transactions and is searchable.
- A dismissal cannot be reversed from the UI. There is no restore endpoint; ask an administrator if a flag was dismissed by mistake.
- Flagged lists show at most about eight items.

---

## Debt payoff planner

**Page:** Debt Payoff (`/debt`). Compares **avalanche** (highest interest rate first, least
interest) with **snowball** (smallest balance first, quickest wins).

**How to use it**

1. Balances are pre-filled from your active credit card, loan, mortgage and manual liability
   accounts. A suggested minimum payment of 2% of the balance (at least $25) is prefilled too.
2. Enter each debt's **APR %** and correct the **Min payment**. Use **Add debt** for debts not
   in FlowLedger and the bin icon to drop one.
3. Enter **Extra per month** you could add on top of the minimums.
4. Click **Compare**. Each strategy shows months to debt-free, total interest and per-debt
   payoff month. If both finish, it shows how much avalanche saves.

**Gotchas**

- APR is **not** known from your bank data; it starts at 0, which makes every strategy
  look free. Always enter real rates.
- The prefilled minimums are guesses; replace them with the real figures.
- A plan that does not finish within 50 years is reported as not feasible (payments do not
  cover the interest).
- Nothing is saved: leaving the page loses your entries.
- Edits here do not change your accounts.

---

## Investments

**Page:** Investments (`/investments`). Performance of each investment account with deposits
and withdrawals removed, so moving money in is not counted as a gain.

**How to use it**

1. Open the page. Each investment account shows its balance and a table for 30 days, 90 days
   and 1 year: starting balance, net deposits, gain or loss, and percent return.
2. Check back after more syncs for longer periods.

**Gotchas**

- It relies on balance history, which builds with each sync. New accounts show "Not enough
  balance history yet", and a short history is labelled (for example "history: 12d").
- It only lists accounts of the investment type. Check account types on the Accounts page.
- Net deposits are inferred from transactions. Brokerage-side moves the data does not show
  can distort the return. Treat the return as an estimate.
- This is a performance view, not a holdings or tax-lot view.

---

## Assets

**Page:** Assets (`/assets`). Physical items you own: vehicles, electronics, jewelry,
property, tools and so on. The [User Guide](user-guide.md#assets) has the full field list;
this section covers the workflow.

**How to use it**

1. Click **Add Asset**, give it a name and category, and fill in the details you have
   (manufacturer, model, serial number, purchase date and price, custom fields).
2. Choose how the value changes over time: manual, depreciates, straight-line or
   appreciates.
3. Open an asset (click its name) to see its value history and record an appraisal, which
   re-anchors the value going forward.
4. Optionally link it to the loan that finances it (to see equity) or to a Manual Asset
   account so that account's balance follows the asset.
5. Use the chart icon on a row to see value over time. **Refresh from feed** asks a
   valuation feed for a new value.

**Gotchas**

- The built-in `model` feed works offline; the generic HTTP `appraiser` feed is off by
  default and needs configuration. Serial numbers are never sent to feeds.
- Dashboard net worth counts each asset once, even when it is linked to an account.
- Removing an asset asks for confirmation. Its valuation history is kept, but it stops
  counting toward net worth.

---

## Query and Ask (natural language)

**Page:** Query (`/query`). Pipe-style queries over your own transactions, accounts and
assets, plus an **Ask in plain English** box. The full language reference is in
[Query and reports](query-and-reports.md).

**How to ask**

1. Type a question in **Ask in plain English**, for example "How much did I spend on dining
   last month?" (up to 500 characters), and press Enter or **Ask**.
2. A local AI model writes a query. FlowLedger checks it against your real data, retrying up
   to three times with the error fed back, then runs it.
3. The query is placed in the editor and run. Read the query to confirm it did what you
   meant, edit it if needed, and choose a **Display** (table, column, bar, line, area,
   donut).
4. Use **Save view** to keep it or **Add to report** to put it into a report. **Ctrl+Enter**
   re-runs the editor.

**Privacy: it runs locally.** Questions are translated by a model on your own machine
(Ollama; default address `http://localhost:11434`, default model `qwen3:8b`, settings under
`Ai:Ollama`). Your question, your category and account names (used to help the model
spell things correctly), and your data stay on the machine; nothing is sent to a cloud AI
service. Queries themselves run in memory, read-only, scoped to your tenant, never against
SQL.

**Gotchas**

- Ollama must be running with the configured model installed. If it is not reachable you
  get a message saying so. The first question after the model has been idle can take a couple
  of minutes while it loads.
- The model can get things wrong. A filter that matches nothing is retried once, and if the
  result is still empty you get a note to check the category or merchant spelling.
- Ask does not change data; it only produces a read-only query.
- Limits: queries up to 4,000 characters, results capped at 5,000 rows, at most 50,000
  transactions scanned per run.
- Spending means `direction == "Debit"`. Add `is_transfer == false` to ignore transfers
  between your own accounts.

---

## Reports

**Pages:** Reports (`/reports`) and a single report (`/reports/{id}`).

A **view** is one saved query with a chart setting. A **report** is a set of 1 to 12 panels.

**How to use it**

1. Build a result on the Query page, then click **Save view** (name, optional description,
   panel title) or **Add to report** (pick an existing report or "New report...").
2. Open Reports to see **My reports and views**, plus **Preset reports** and **Preset
   views** that ship with the app.
3. Click **Open** to run a report. Each panel is run fresh; use **Refresh** to re-run.
4. Click the query icon on a panel (or **Edit query** on a view) to edit it in the workbench.
5. Presets are read-only: use **Save a copy** to get an editable version.

**Gotchas**

- A failing panel shows a warning on that panel and does not break the rest of the report.
- You cannot delete the last remaining panel of a report from its page (the delete button is
  disabled); delete the whole report from the Reports list instead.
- Deleting a report or view cannot be undone (a confirmation is shown).
- Saved items are per user account; linked logins share them, other tenants do not.

---

## Bill Sheet

**Page:** Sheet (`/sheet`). Your recurring bills, subscriptions and debts as a spreadsheet
with a column per month. The matching rules and CSV import format are explained in the
[User Guide](user-guide.md#sheet-spreadsheet-mode).

**How to use it**

1. Open the page. FlowLedger refreshes the sheet and adds detected monthly expenses under
   **Detected**. Use the arrows to move between months.
2. Click **Add row** to add a bill: name, section, payer, how often it repeats (once a
   month, twice a month, every week, or every N weeks), due day, estimated and actual cost,
   flags (Debt, Perpetual, Optional, Business, Auto-pay) and **Match text**.
3. Click **Update from payments** to re-check recent transactions and tick rows.
4. Click a month cell to override it. The cycle is: auto tick, then manual "not paid", then
   manual tick, then back to automatic. Manual choices are never changed by automation.
5. Use **Import** to load a CSV export of your existing workbook.

**Gotchas**

- Match text is the key: words from the bank description separated by `|`. If a bill never
  ticks, fix the match text first. Amounts are not compared.
- For weekly and every-N-weeks bills the estimate is **per payment**; the sheet counts every
  payment that falls in the month.
- Only the last four digits of an account number are shown.
- A detected row you delete stays deleted.
- A snackbar after Update tells you how many were ticked, cleared and newly detected.

---

## Data and Backups (owner only)

**Page:** Data & Backups (`/data`). Visible only to the household owner, meaning the login
that owns the original household data. Other users do not see the menu entry, see an
information message if they open the URL, and the API returns 403 regardless.

**What it shows**

- **Backups**: healthy or not (healthy means a successful backup in the last 36 hours),
  whether an off-site restic copy is configured, last run, rows backed up, restore drill
  result and off-site result.
- **Back up now**: runs a backup immediately and verifies it.
- **Export all data (.zip)**: one CSV per table plus a manifest with hashes.
- **Bank connection usage**: provider calls today and this month against your configured
  budgets, per-endpoint counts and a pause (kill switch) indicator.
- **Recent backup runs**.

**How to use it**

1. Open Data & Backups and check the green "Healthy" banner.
2. If the off-site copy says "NOT configured", set it up per
   [Backups and recovery](self-hosting/backups-and-recovery.md#turning-on-off-site-backups-restic--backblaze-b2).
3. Click **Back up now** before risky changes (for example before switching to real data).
4. Click **Export all data** for a portable copy.

**Restore**

There is **no restore button** in the app. Restore is an operator task: install onto an empty
database and import the export or a backup snapshot via the API (`POST /api/data/import`).
Step by step: [Disaster recovery](self-hosting/backups-and-recovery.md#disaster-recovery-new-machine-empty-database).

**Gotchas**

- Backups and export cover the **whole installation**, not just one user.
- Import only works into an **empty** database.
- The data endpoints accept requests only from the host itself unless an administrator sets
  `DataAdmin__AllowRemote=true`. If Export or Back up now fails from a remote browser, that is
  likely why.
- If sign-in is on but no owner has been configured (`Tenancy__LegacyOwner*`), nobody is the
  owner and the page is unavailable.
- Provider usage numbers only matter for pay-per-call providers.

---

## Linked logins

**Page:** Linked logins (`/account/logins`). Lets two or more sign-ins (for example your
work and personal identity-provider accounts) see the **same** FlowLedger data.

**How to link a second login**

1. On your **main** login, open Linked logins and click **Generate code**.
2. Sign out, then sign in with the **other** login.
3. Open Linked logins on that login, enter the code under **Use a code from another
   login**, and click **Link this login**. You are signed in again silently to pick up the
   shared data.

**To unlink**, click **Unlink** beside a login in the list (shown only when there is more
than one login and not on the one you are using).

**Gotchas**

- Codes are single use, expire after 15 minutes and are stored hashed.
- Everything saved under the second login before linking stays stored but is not shown after
  linking. It is not deleted.
- Another person, such as a spouse, who signs in with their own login gets their own
  **separate** data. Do not link their login unless you want them to see everything.
- Linked logins have identical access: there is no read-only or admin role. (Shared
  households with roles are listed as future work in ADR 0008.)
- The page only makes sense when sign-in is enabled. Without sign-in there is only one household, so there is nothing to link.
