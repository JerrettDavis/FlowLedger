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

## Threat model highlights

- Tenant data isolation is enforced at the database query level via EF Core global filters.
- Provider tokens (e.g. MX) are stored encrypted; raw credentials are never persisted.
- No sensitive financial amounts or account numbers are emitted to logs or telemetry.
- SSRF protections are required on any URL-accepting import or webhook path.
