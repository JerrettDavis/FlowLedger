# ADR 0003 — MX Integration Behind Feature Flag

## Context
FlowLedger needs a real bank data provider (MX.com) but API calls cost money and require credentials. Development and CI must work without MX credentials.

## Decision
MX integration is placed behind the `Mx:Enabled` feature flag. When false (default), the SimulatedProvider generates deterministic fake data. When true, MxProvider is registered and the real MX.com API is used.

## Consequences
- Positive: CI and local dev work with zero credentials; no accidental API charges.
- Positive: Swapping providers requires one config change, no code changes.
- Negative: Real MX behavior is only testable with credentials (a separate integration test project FlowLedger.Integrations.Tests covers this with a live-sandbox skip trait).
