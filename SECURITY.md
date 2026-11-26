# Security Policy

## Supported versions

| Version | Supported |
|---|---|
| main | Yes |
| tagged releases | Latest release only (once releases are cut; see CHANGELOG.md) |

## Reporting a vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Report privately through GitHub's private vulnerability reporting: <https://github.com/JerrettDavis/FlowLedger/security/advisories/new>. If you cannot use it, open a minimal public issue asking for a private contact channel (without any details of the vulnerability).

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
