# Architecture decision records

Each ADR has a `Status:` line near the top. Statuses used: Accepted, Accepted and implemented, Superseded (with the
superseding ADR named). Add a new ADR as the next number and add it to this table in the same change.

| ADR | Title | Status |
|---|---|---|
| [0001](0001-auth-openiddict-deferred.md) | Defer OpenIddict integration to Phase 6 SaaS milestone | Accepted (sign-in part superseded by 0008) |
| [0002](0002-bdd-library.md) | BDD test library selection | Accepted |
| [0003](0003-mx-behind-feature-flag.md) | MX integration behind feature flag | Accepted |
| [0004](0004-sync-cursor-persistence.md) | Sync cursor persistence | Accepted |
| [0005](0005-aspire-vs-compose.md) | Aspire for dev inner loop, Docker Compose for self-hosting | Accepted |
| [0006](0006-dark-mode-palette-lifted-weights.md) | Dark-mode palette uses lifted 400-weight variants | Accepted |
| [0007](0007-raw-payload-retention.md) | Raw provider payloads are retained indefinitely | Accepted |
| [0008](0008-multi-user-oidc-tenancy.md) | Provider-agnostic multi-user (OIDC) with per-user tenants | Accepted |
| [0009](0009-multi-provider-self-service-linking.md) | Multi-provider readiness (self-service bank linking) | Accepted (design only; implementation deferred) |
| [0010](0010-connectors-per-instance-accounts-per-connector.md) | Connectors enabled per instance, linked per account | Accepted and implemented |
| [0011](0011-account-tracking-settings.md) | Account tracking settings | Accepted and implemented |
| [0012](0012-home-assistant-alerts.md) | Home Assistant alerts | Accepted and implemented |
| [0013](0013-local-in-process-ai.md) | Local, in-process AI assistance | Accepted |
| [0014](0014-duplicate-detection-and-merge.md) | Duplicate detection, suggested merges and safe auto-merge | Accepted |
| [0015](0015-strategy-engine-and-simulators.md) | Strategy engine, market rates and simulators | Accepted |
| [0016](0016-integrations-base.md) | Integration base layer (REST, SOAP/WSDL, signed webhooks) | Accepted and implemented |
| [0017](0017-quiltt-embedded-connector.md) | Quiltt embedded connector | Accepted (code complete; waiting on production keys) |

## Decisions with no ADR yet

These areas shape the system but have no written decision record: backups and data safety (see
`docs/self-hosting/backups-and-recovery.md`), observability, database-level tenancy enforcement, and release/versioning.
