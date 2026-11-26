# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| main | Yes |

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Report privately by emailing the maintainers (contact in the repository profile) or using GitHub's private vulnerability reporting feature.

Include:

- Description of the vulnerability.
- Steps to reproduce.
- Impact assessment.
- Suggested fix (optional).

We aim to acknowledge within 48 hours and provide a resolution timeline within 5 business days.

## Sensitive data scope

FlowLedger handles: financial institutions, account balances, transactions, income, debt, goals, and provider credentials. Any vulnerability affecting this data is treated as high severity.

## Security controls and known gaps

This section states what the code does today. Each control names the file or test that backs it; anything without one is a known gap, not a control.

Implemented:

- **Tenant isolation.** Tenant-owned entities are filtered by EF Core global query filters (`src/FlowLedger.Infrastructure/Persistence/FlowLedgerDbContext.cs`). Isolation is tested per feature (for example `tests/FlowLedger.Api.Tests/Security/DataAdminTenantTests.cs`, `tests/FlowLedger.Api.Tests/Security/ProductionTenantTests.cs`, `tests/FlowLedger.Infrastructure.Tests/Sync/SimpleFinTenantIsolationTests.cs`). There is no systematic route-by-route isolation sweep yet.
- **Encrypted secrets stored in the database.** Home Assistant webhook URLs and integration credentials/signing secrets are encrypted with AES-256-GCM using `HomeAssistant:EncryptionKey` / `Integrations:EncryptionKey`, and are refused (not stored in clear text) when no key is configured (`src/FlowLedger.Infrastructure/DataSafety/NpgsqlHomeAssistantStore.cs`, `src/FlowLedger.Infrastructure/DataSafety/NpgsqlIntegrationStore.cs`).
- **Provider credentials are not stored in the database.** MX, Quiltt and SimpleFIN credentials (including the SimpleFIN access URL) are supplied through configuration or environment variables only. The `provider_connections` table stores provider member ids, institution and status, not credentials (`src/FlowLedger.Infrastructure/Persistence/Entities/ProviderConnectionRecord.cs`).
- **SSRF checks on user-supplied URLs.** Integration and Home Assistant webhook URLs reject link-local/metadata/multicast targets, reserve loopback/private targets for the household owner, and require https for internet-reachable targets (`src/FlowLedger.Application/Features/Integrations/IntegrationUrl.cs`, `src/FlowLedger.Application/Features/HomeAssistant/HomeAssistantWebhookUrl.cs`).

Not implemented (do not rely on these):

- **No log redaction pipeline.** `SensitivePropertyMaskingEnricher` (`src/FlowLedger.Api/Logging/`) is an unwired Serilog enricher: the app logs through the default Microsoft logger and OpenTelemetry, neither of which uses it. It is covered only by direct unit tests. Do not assume amounts, account numbers or names are kept out of logs or telemetry. Known examples: SimpleFIN provider error strings can contain institution names, and a malformed MX webhook body is logged as an exception. Treat logs and any OTLP backend as sensitive.
- **No encryption at rest in the application.** The PostgreSQL database, backups/exports and the raw provider payload archive hold financial data and PII in plaintext. Raw provider payloads are retained indefinitely (ADR 0007) and there is no tenant offboarding or purge path. Protect volumes, backups and the archive directory with disk/volume encryption and access controls.
- **No documented threat model, data inventory or incident runbooks yet** (tracked in the production-readiness backlog).
