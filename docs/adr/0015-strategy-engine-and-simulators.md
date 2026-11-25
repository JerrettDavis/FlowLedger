# ADR 0015: Strategy engine, market rates and simulators

## Status
Accepted

## Context
FlowLedger tracks balances, spending and debt but did not turn that data into actions. Users wanted suggestions such as
moving idle cash to higher-yield accounts, CD ladders, refinance and consolidation checks, and what-if simulators for past
and future choices.

## Decision
- **Pure rules engine** (`StrategyEngine`) maps a `StrategyInputs` snapshot to ranked `Suggestion`s. Each carries evidence,
  steps, assumptions, confidence and an optional annual dollar impact, so numbers are checkable. No I/O, fully unit tested.
- **Market rates** come from `IMarketRateProvider`. `FredMarketRateProvider` reads public FRED CSV series (Freddie Mac mortgage,
  Treasury yields, Fed consumer loan and credit card rates) with no key and no user data sent. Savings/money market/CD yields are
  *estimated* from Treasury yields minus a spread. Any failure falls back per-field to labelled assumptions. Cached 6 h.
- **Simulators** (`Simulators`, `LoanMath`, `LadderPlanner`): forward growth comparison, historical replay ("what if I'd saved
  more / cut a category / moved idle cash"), refinance break-even and lifetime interest, CD ladder versus savings.
- Per-account interest rates and loan APRs are not in the ledger, so the engine uses typical or market rates and says so;
  the simulators accept the user's real figures.
- Recommendations are conservative: a ladder is not suggested when the yield curve is flat or inverted, and the report always
  carries a not-financial-advice disclaimer with FDIC/NCUA limits.

## Consequences
- Suggestions improve when per-account APR and loan terms are captured (future work: account rate fields, then exact refinance
  and consolidation impacts).
- The LLM layer (ADR 0013) can later narrate suggestions; the engine's structured output is the source of truth, the model never
  invents numbers.
- Outbound requests go only to `fred.stlouisfed.org` for public series.
