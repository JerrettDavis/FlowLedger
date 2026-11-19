# Getting Started as a New User: Your First 30 Minutes

This walkthrough takes you from first sign-in to a working forecast. It assumes FlowLedger
is already installed and running (see [Getting Started](getting-started.md) or
[Docker Compose self-hosting](self-hosting/docker-compose.md)). For a tour of each page see
the [User Guide](user-guide.md) and the [Advanced User Guide](user-guide-advanced.md).

Items marked **(verify)** come from reading the source and have not been confirmed on a
running install.

---

## Minute 0 to 5: Sign in

1. Open FlowLedger in your browser. If your administrator enabled single sign-on, you are
   redirected to your identity provider (authentik, Keycloak, Entra ID, Google or any
   OpenID Connect provider). Sign in there.
2. After sign-in, FlowLedger creates your private workspace on first login. Your name shows
   in the top bar next to **Sign out**.
3. If your installation has no sign-in configured it runs in single-household mode with no
   login. That is meant for development only.

What to expect on first sign-in:

- **You are the original household owner** (your login is configured as the owner): you
  see the existing household data, plus **Data & Backups** in the menu.
- **You are anyone else** (for example a spouse with their own login): you start with an
  **empty, separate** workspace. You will not see the owner's data, and you will not see
  Data & Backups. If you are meant to share the owner's data, ask them for a link code (see
  [Linked logins](user-guide-advanced.md#linked-logins)) and enter it at
  `/account/logins`.

## Minute 5 to 10: Connect your bank and run the first sync

The top bar changes depending on the environment, shown as a chip:

| What you see | Meaning | What to do |
|---|---|---|
| **DEMO DATA** chip and **Connect & Sync** | Built-in simulated bank with fictional data | Click **Connect & Sync** to load a demo household and explore safely |
| **SANDBOX DATA** chip | Provider test environment | Use it to practice; data is test data |
| **Connect bank** and **Sync now**, no chip | Production with a real provider | Follow the steps below |

For real data:

1. Click **Connect bank**.
   - With **MX**, a dialog warns that creating the connection is a billable provider call
     that is made only once. Click **Continue**; your provider's secure widget opens in a
     new tab. You sign in to your bank there, not in FlowLedger.
   - With **SimpleFIN Bridge**, the Bridge site opens in a new tab. Link or re-authenticate
     your institutions there.
2. Finish linking, return to FlowLedger, and click **Sync now**.
3. A progress spinner shows while it syncs. Afterwards open **Accounts** to confirm your
   accounts and balances arrived.

Good to know:

- **Only the household owner can link a bank.** Provider credentials are tied to the
  household tenant; other users' syncs are empty by design. Per-user linking is listed as
  future work in [ADR 0008](adr/0008-multi-user-oidc-tenancy.md).
- Syncs happen automatically: when you open the app and the last sync is over an hour old,
  then hourly while a tab is visible, plus a daily background sync. Real providers allow at
  most one sync every 30 minutes (a too-soon request is refused, nothing is sent to the
  provider), and each provider call counts against a daily and monthly budget visible on
  Data & Backups.
- How much history the first sync brings depends on the provider (SimpleFIN requests are
  capped at 90 days per window). Late-posting items are picked up by re-reading the recent past each time; duplicates are
  filtered out.

## Minute 10 to 15: Check your accounts and fix account types

1. Open **Accounts**. Check each account's **Type** (Checking, Savings, Credit, Investment,
   Mortgage and so on). A padlock means you set the type manually and sync will not change it.
2. Fix any wrong types with the edit icon. This matters: types decide what counts as
   spending, which accounts are debts for the [Debt planner](user-guide-advanced.md#debt-payoff-planner)
   and which appear on [Investments](user-guide-advanced.md#investments).

## Minute 15 to 20: Review your categories

1. Open **Transactions** and click **Auto-categorize**. It fills only transactions that have
   no category (transfers between your accounts, merchants you have categorized before,
   built-in keywords). The result toast says how many were categorized and how many are left
   for you.
2. Open **Spending** and set the period to Last 90 days. Click the category chips for
   anything wrong or **Uncategorized**, pick the right category, and choose **All
   transactions from "merchant"** so future syncs follow. See
   [Recategorizing](user-guide-advanced.md#recategorizing-a-transaction).
3. Open **Categories** to tidy names: rename `Food/Dining` to whatever you prefer, or merge
   duplicates. Remember that merges cannot be undone.
4. Add a couple of [Rules](user-guide-advanced.md#rules) for recurring oddities (for example
   `venmo` to your chosen category).

Ten or twenty merchants account for most of your transactions, so a few minutes here pays off
for every chart and budget later.

## Minute 20 to 25: Set up recurring flows

Recurring flows (paychecks, rent, subscriptions) drive the Money Plan and every forecast.

1. Open **Recurring Flows**. If suggestions appear under **Suggested recurring flows**,
   those were found in your transactions. Add the ones that are real.
2. Click **Detect now** to re-run detection after your categories are tidy.
3. Click **Add Flow** for anything missing: amount, frequency, start date and account.
4. Optional: open **Sheet** to see the same bills as a month-by-month paid tracker. It
   builds a **Detected** section automatically. See [Bill Sheet](user-guide-advanced.md#bill-sheet).

## Minute 25 to 28: Set a budget

1. Open **Budgets**. Under **Suggestions** click a chip for one of your top spending
   categories. It fills a limit slightly above what you have spent this month.
2. Adjust the limit to what you want to spend and click **Save budget**. Start with two or
   three categories; you can add more later.
3. At 80% of a limit you will see a warning on the Dashboard, and at 100% an error.

## Minute 28 to 30: Look at your forecast

1. Open the **Dashboard** for the Balance Projection and the overdraft indicator.
2. Open **Forecasts** to adjust the horizon and see low points.
3. If a dip below zero shows, open **Money Plan** to see which planned row causes it.

You are set up. Next steps worth exploring:

- [Goals](user-guide-advanced.md#goals) for savings targets.
- [Assets](user-guide-advanced.md#assets) to include your home or vehicles in net worth.
- [Query and Ask](user-guide-advanced.md#query-and-ask-natural-language) for custom questions.
- On the Dashboard, review anything under **Suspicious Activity** and dismiss it with a note
  once you have checked it ([details](user-guide-advanced.md#suspicious-activity-and-dismissing-with-a-note)).
- If you are the owner, open **Data & Backups** and make sure the banner is green and an
  off-site copy is configured.

---

# FAQ

## Privacy and data

**What data does FlowLedger store?**
Everything needed to run the features, in your own database (PostgreSQL) on infrastructure
you control: accounts, balances and balance history, transactions (descriptions, amounts,
dates, categories, merchant names, tags, notes, splits), recurring flows, budgets, goals,
assets (including optional serial numbers and purchase details), saved queries and reports,
bill sheet rows (only the last four digits of account numbers are shown), and the list of
logins mapped to your workspace. Sign-in identities (issuer, subject, display name, email)
come from your identity provider.

**Does it keep the raw bank responses?**
Yes. Every response from the bank-aggregation provider is saved unmodified to a file archive
**before** it is parsed, and nothing in the app deletes it (see
[ADR 0007](adr/0007-raw-payload-retention.md)). The archive can contain personal and
financial detail, so protect that directory (default `~/.flowledger/objects`) the same way
you protect the database. Backups include it.

**Does FlowLedger store my bank password?**
Your bank login is entered on the provider's own connection widget or site (MX or SimpleFIN
Bridge), not in FlowLedger. What FlowLedger holds is the provider connection reference
(for MX) or the SimpleFIN access URL, which is kept in configuration/secrets rather than the
database. Treat that access URL like a password. **(verify)** the exact storage for your
provider before relying on this in a compliance setting.

**Is the bank access read-only?**
FlowLedger's provider integration only reads: it begins a connection, lists accounts,
fetches transactions, and receives webhooks. It has no operation to move money or initiate
payments. Whether the underlying provider grants additional permissions is governed by that
provider's own terms, so check your provider's consent screen. **(verify)** against your
provider's documentation.

**Does any of my data leave my machine?**
Not by default. Two things talk to the outside world, both by your choice: the bank
aggregation provider you enable (to fetch your data), and the optional off-site backup you
configure, which is encrypted on your machine by restic before upload so the storage
provider only sees ciphertext. Natural-language [Ask](user-guide-advanced.md#query-and-ask-natural-language)
uses a **local** model (Ollama) and sends nothing to a cloud AI service. An optional
asset valuation feed, if you turn it on, makes requests to the URL you configure, but serial
numbers are never sent.

**Who can see what in a multi-user setup?**
- Each person who signs in gets their own **private workspace** (a tenant). Everything is
  partitioned by workspace; users cannot see each other's data.
- **Linked logins** (several sign-ins you attach to one workspace with a link code) all see
  the same data and have the same permissions. There is no read-only or admin role yet.
- The **original household owner** has extra visibility of installation-wide tools: Data &
  Backups, whole-database export and backup, and the only workspace where provider
  credentials sync. Other users get a 403 from those tools.
- The **person running the server** (the administrator) can technically reach the database
  and backups. Multi-user mode isolates users from each other inside the app; it is not
  protection from the operator.
- Details: [ADR 0008](adr/0008-multi-user-oidc-tenancy.md) and
  [Multi-user sign-in](self-hosting/multi-user-auth.md).

**How do I delete my data?**
There is currently **no single "delete my account" button**. **(verify)** whether this has
since been added. What you can do:

- *Remove individual items in the app*: delete budgets, goals, rules, saved reports and
  assets (an asset's valuation history is kept). Account and transaction deletion is not
  offered in the pages reviewed. **(verify)**
- *Disconnect the bank*: revoke access with your provider (the MX or SimpleFIN Bridge site)
  so no new data is fetched. Removing data already fetched is a separate step.
- *Remove everything on a self-hosted install*: you control the database. Stopping the stack
  and removing its volumes wipes the database (for Docker Compose, `docker compose --profile
  full down -v`, per [docker-compose.md](self-hosting/docker-compose.md#stopping-and-cleaning-up)).
  Also delete the raw payload archive directory and any local backup folder
  (`~/.flowledger/backups`), and delete or prune the off-site restic repository if you
  configured one.
- *On a shared server*, ask the administrator to remove your workspace's rows; there is no
  in-app tool for it today.

**Are my backups safe?**
Backups run daily: a consistent snapshot, an automatic restore test, and (if configured) an
encrypted off-site copy. The Data & Backups page shows whether the last run succeeded. Keep
the off-site repository password in a password manager; without it the copies cannot be
decrypted. Details: [Backups and recovery](self-hosting/backups-and-recovery.md).

## Using the app

**Why is my data different from my bank's website?**
Syncs are periodic (not instant), pending transactions can change when they post, and
providers sometimes lag. Press **Sync now** and compare again. Account balances are the
provider's reported balances.

**Why are transfers excluded from my spending?**
Money moving between your own accounts, investment contributions and card payments is not
spending. FlowLedger detects these and leaves them out of totals. Use the Include
transfers & investments switch on Spending if you want to see them.

**I recategorized a merchant but an old transaction did not change.**
Transactions you categorized by hand are protected from automation and from the gentler
merchant checkbox on Transactions. Use **All transactions from "merchant"** in the Spending
page's Change category dialog, which overrides earlier manual choices. See
[precedence](user-guide-advanced.md#how-a-transaction-gets-its-category-precedence).

**The Ask box says the AI model is not reachable.**
Ask needs a local Ollama server running with the configured model installed. Ask your
administrator, or see the [advanced guide](user-guide-advanced.md#query-and-ask-natural-language).
You can always write a query by hand in the workbench.

**A bill on the Sheet will not tick.**
Edit the row and fix its **Match text** so it contains words from the bank description,
separated by `|`.

**I cannot see Data & Backups.**
It is only for the household owner. See [Linked logins](user-guide-advanced.md#linked-logins)
if you are the owner on a second login.

**Can I try it without real bank data?**
Yes. A fresh install defaults to the simulated provider with fictional data, and the
**Connect & Sync** button loads it. Real data should go in a separate database from demo
data; see [Switching from demo/sandbox to real data](self-hosting/backups-and-recovery.md#switching-from-demosandbox-to-real-data).
