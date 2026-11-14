# Assets (physical holdings) — design

**Intent:** track physical items (vehicles, electronics, firearms, property…) with purchase info, a depreciation/appreciation model and valuation history; link them to accounts; feed net worth; surface an overview card on the dashboard.

**Non-goals:** insurance management, document/photo storage, tax depreciation schedules, real third-party appraiser integrations (a generic config-driven feed is provided, disabled by default).

**Approaches considered:** (a) model assets as `Account(ManualAsset)` rows — rejected, accounts have no serial/model/history/valuation model; (b) new `Asset` aggregate with owned valuation history — **chosen**; (c) event-sourced valuations — overkill.

**Calls made**
- `Asset` aggregate (tenant-scoped). `CurrentValue`/`ValuedOn` = latest recorded valuation (the anchor). `AssetValuationEstimator` projects forward by method: Manual, StraightLine (salvage + useful life), DecliningBalance, Appreciation. Money stays decimal; the only double math is isolated in one compounding helper and rounded to cents.
- Fixed fields: manufacturer, model, serial, notes, purchase date/price. Custom fields: string dictionary (jsonb), capped at 25. `GET /api/assets/categories` returns suggested fields per category (e.g. Firearm → caliber, action; Vehicle → VIN, mileage).
- Account link: `LinkedAccountId`. A **ManualAsset** account carries the asset's value (recording a valuation pushes the balance to it, so net worth counts it once). A **Mortgage/Loan/CreditCard** link is informational: equity = value − liability, and the liability is already in account net worth.
- Net worth is computed server-side by one pure `NetWorthCalculator` (`GET /api/assets/summary`); Home consumes it and falls back to the old client formula.
- Feeds: `IAssetValuationFeed`. Built-in `ModelEstimateFeed` (no network) and `HttpJsonAppraisalFeed` (generic URL template + JSON path, API key from config, **disabled by default**, never sends serial numbers). Refresh via `POST /api/assets/{id}/refresh?feed=`.
- Privacy: serial numbers are never logged and never sent to external feeds.
