# Production Hardening & Secrets

This guide is the ordered runbook for exposing a FlowLedger instance to real users or real
financial data. Follow the steps in [Go-live runbook](#go-live-runbook) in order; the sections
after it are the reference material each step points to. Read it before switching any real
provider on (`Mx__Enabled`, `SimpleFin__Enabled`, `Quiltt__Enabled`) in a non-dev environment.

> **Compose forwards only some settings.** `docker-compose.yml` passes an explicit list of
> variables to the containers and hard-codes `Mx__Enabled=false`. Settings that are not in
> that list (providers, backups, `Integrations__EncryptionKey`, `ForwardedHeaders__Enabled`,
> `Worker__MaxParallelTenants`, ...) must be added to the `environment:` of the `api`/`worker`/`web`
> services in a `docker-compose.override.yml`. See [.env.example](../../.env.example).

## Go-live runbook

1. **Secrets and database.** Set `POSTGRES_PASSWORD` and `API__KEY` to strong random values
   (`openssl rand -hex 32`). Keep Postgres on loopback or a private network and enable TLS if it
   is remote ([database.md](database.md)). See [Supplying secrets](#supplying-secrets-in-production).
2. **Sign-in.** Configure OIDC (`Auth__Oidc__*`, [multi-user-auth.md](multi-user-auth.md)). Without
   it the Web UI has no login and refuses to start outside Development. Set
   `AUTH__ALLOWANONYMOUS=true` only on a trusted single-user host where every port stays bound
   to `127.0.0.1`. With OIDC on, `API__TENANTSIGNINGKEY` (same value on api and web, >= 32 characters) is **required** and the stack refuses
   to start without it; decide who may sign up (`AUTH__OIDC__ALLOWEDEMAILS__0`, see [Multi-user hardening](#multi-user-hardening-secure-defaults)).
3. **TLS and proxy.** Put a TLS reverse proxy in front of the Web UI
   ([TLS termination](#tls-termination)) and, if the proxy is the only path to the Web port, set
   `ForwardedHeaders__Enabled=true` ([Reverse proxy / TLS](#reverse-proxy--tls)).
4. **Encryption keys.** Set `HomeAssistant__EncryptionKey` (and `Integrations__EncryptionKey` if you
   use integrations) to `openssl rand -base64 32`, the same value on `api` and `worker`. Without a
   key FlowLedger refuses to store webhook URLs and integration credentials.
5. **Backups.** Configure off-site backups (restic, `Backup__Restic__*`, `B2_ACCOUNT_*`) and run
   **Back up now** on the Data & Backups page; confirm it shows an off-site copy. Back up the raw
   payload archive volume (`objects_data`) as well. Details and recovery:
   [backups-and-recovery.md](backups-and-recovery.md). The built-in backup is a daily logical
   snapshot (RPO up to 24 hours; see [database.md](database.md)).
6. **Fresh database for real data.** Real data must go into a database that never held demo or
   sandbox data. The `data_environment` marker (`demo` / `sandbox` / `production`) makes the API and
   Worker refuse to start when it disagrees with the provider settings; follow
   [Switching from demo/sandbox to real data](backups-and-recovery.md#switching-from-demosandbox-to-real-data).
7. **Providers.** Enable exactly the provider you intend, with its credentials from a secrets
   manager: MX ([mx-integration.md](../architecture/mx-integration.md), `Mx__Environment=production`,
   `Mx__BaseUrl=https://api.mx.com`), SimpleFIN (`SimpleFin__Enabled`, `SimpleFin__AccessUrl`), or
   Quiltt (`Quiltt__Enabled`, `Quiltt__Environment=production`,
   [ADR 0017](../adr/0017-quiltt-embedded-connector.md)). Set the API cost budgets
   (`Mx__Provider__DailyCallBudget`, `Mx__Provider__MonthlyCallBudget`) if you use MX.
8. **Data admin endpoints.** `GET /api/data/export`, `POST /api/data/import` and
   `POST /api/data/backup-now` accept loopback callers only. Leave `DataAdmin__AllowRemote` unset
   unless you understand the exposure; never publish these through the proxy.
9. **Observability.** Wire `OTEL_EXPORTER_OTLP_ENDPOINT` if you want traces, metrics and logs
   exported. Application logs are not redacted (see [SECURITY.md](../../SECURITY.md)); treat the
   telemetry backend as sensitive.
10. **Verify** with the checklist below, then take a fresh backup.

### Checklist before going live

- [ ] `POSTGRES_PASSWORD` is a strong random value (compose refuses to start without one).
- [ ] `API__KEY` is a strong random value (not `dev-local-key-not-for-production`).
- [ ] OIDC sign-in is configured, or `AUTH__ALLOWANONYMOUS=true` is set deliberately on a trusted single-user host.
- [ ] With OIDC: `API__TENANTSIGNINGKEY` is set, `API__TENANTID` is a random GUID (or `TENANCY__ALLOWWELLKNOWNLEGACYTENANTID=true` acknowledges an existing install), sign-up is InviteOnly/Closed (no `AUTH__SIGNUP__LEGACYOPEN`), and no startup Warning about open sign-up or unsigned tenants remains.
- [ ] Postgres and Redis ports are not exposed to the public internet; Postgres uses TLS if remote.
- [ ] TLS termination is in place; all HTTP redirects to HTTPS; only the Web UI (and provider webhook endpoints) are exposed.
- [ ] `ForwardedHeaders__Enabled=true` if a proxy terminates TLS in front of the Web app.
- [ ] `HomeAssistant__EncryptionKey` / `Integrations__EncryptionKey` are set (if those features are used) and shared by api and worker.
- [ ] Real data is in a fresh database; the `data_environment` marker is `production`.
- [ ] Exactly one real provider is enabled and points at its production endpoint (e.g. `Mx__BaseUrl=https://api.mx.com`, `Quiltt__Environment=production`); `Mx__Enabled=false` if you are not using MX.
- [ ] Secrets live in a secrets manager, not in a `.env` on the server.
- [ ] Off-site backup (restic) is configured, **Back up now** succeeded and the Data & Backups page is green.
- [ ] The `postgres_data` and `objects_data` volumes are covered by your host backups or snapshots.
- [ ] `DataAdmin__AllowRemote` is unset and `/api/data/*` is not reachable through the proxy.
- [ ] `OTEL_EXPORTER_OTLP_ENDPOINT` is set if you want telemetry exported.

## The simulated-vs-real toggle

FlowLedger ships with `Mx:Enabled=false`. In this state:

- The Simulated provider generates deterministic fake data.
- No MX API calls are made and no charges are incurred.
- No MX credentials are required.

Setting `Mx__Enabled=true` flips the app to the real MX.com API. That single flag is the
only code-path difference between demo mode and live bank data. The full walkthrough for
making that switch is in [mx-integration.md](../architecture/mx-integration.md).

## Supplying secrets in production

**Never use .NET user-secrets in production.** User-secrets are stored in a plain-text
JSON file on the developer's machine and are not suitable for server or container
deployments.

Instead, pick one of these approaches depending on your infrastructure:

### Environment variables (simplest)

Set secrets as environment variables in your container runtime, OS service, or
deployment platform. The compose variable names use `__` (double underscore) as the
section separator, which .NET maps to `:` in configuration:

```
POSTGRES_PASSWORD=<strong-random>
API__KEY=<strong-random>
Mx__Enabled=true
Mx__ApiKey=<from-mx-dashboard>
Mx__ClientId=<from-mx-dashboard>
Mx__BaseUrl=https://api.mx.com
Mx__WebhookSecret=<from-mx-dashboard>
```

Generate strong random values with:

```bash
openssl rand -hex 32
```

### Orchestrator / platform secrets

| Platform | Mechanism |
|----------|-----------|
| Kubernetes | `Secret` objects, mounted as env vars or files; use an external secrets operator (ESO, Sealed Secrets) to sync from a vault. |
| Docker Swarm | `docker secret create` + `secrets:` stanza in the compose/stack file. |
| AWS | AWS Secrets Manager or SSM Parameter Store, loaded at container startup via the AWS Secrets Manager integration for ECS/EKS. |
| Azure | Azure Key Vault with Managed Identity; the .NET Azure Key Vault configuration provider loads secrets at startup. |
| HashiCorp Vault | Vault Agent sidecar or the Vault .NET SDK configuration provider. |

### Never commit secrets to source control

- `.env` is gitignored — keep it that way.
- Never hard-code API keys in `appsettings.json` — use environment variables or a
  secrets manager to override configuration at runtime.
- Audit your history before making the repo public: use `git log -S "<secret>"` to
  verify a secret was never committed.

## TLS termination

The FlowLedger containers listen on port 8080 (HTTP). Do not expose that port directly
to the internet.

Place a TLS-terminating reverse proxy in front:

| Option | Notes |
|--------|-------|
| **Caddy** | Automatic Let's Encrypt certificates; minimal config. Recommended for simple self-hosting. |
| **Traefik** | Native Docker/Kubernetes integration; automatic cert management. |
| **nginx** | Widely documented; pair with certbot for Let's Encrypt. |
| **Cloud load balancer** | AWS ALB, GCP HTTPS LB, Azure Application Gateway — manage certs via their certificate managers. |

Ensure the proxy:
- Redirects all HTTP (port 80) traffic to HTTPS (port 443).
- Sets `X-Forwarded-Proto: https` and `X-Forwarded-For` so that ASP.NET Core's
  `UseForwardedHeaders()` picks them up correctly.
- Terminates TLS before forwarding to the Web container on `127.0.0.1:5002`. Expose only
  the Web UI (and, if you use MX, `POST /api/integrations/mx/webhooks` on the API); the
  rest of the API is a backend for the Web and should stay private.
- Is required for remote sign-in: outside Development the auth cookie is `Secure`-only, so
  OIDC sign-in over plain HTTP from another device will fail.

## Rotating `API__KEY` without downtime

`API__KEY` is the bearer token shared between the Web frontend, the Worker and the API. Outside
Development the API refuses to start unless the key is at least 32 characters and is not a
well-known placeholder (`changeme...`, `password...`, `dev-local-key...`, `ci-test-api-key...`).
Generate one with `openssl rand -hex 32`.

The API also accepts an optional `API__PREVIOUSKEY` (`Api:PreviousKey`) so rotation does not need
every service restarted at the same instant:

1. Generate the new key: `openssl rand -hex 32`
2. On the **API**: set `API__PREVIOUSKEY` to the *old* key and `API__KEY` to the *new* key, restart it.
   Callers still sending the old key keep working; each such request logs a Warning
   ("authenticated with Api:PreviousKey").
3. Roll `web` (and `worker`) with `API__KEY` set to the new key.
4. When the Warnings stop, remove `API__PREVIOUSKEY` from the API and restart it. The old key is
   rejected from then on. The previous key must also meet the strength rules and differ from the new key.

Rejected keys are logged at Warning (method, path, remote IP, never the key) and counted in the
`flowledger.api.auth.failures` metric (meter `FlowLedger.Api`).

## Rotating `Mx__ApiKey` / `Mx__ClientId` / `Mx__WebhookSecret`

MX credentials are issued by MX and can be rotated in the MX dashboard.

### API key and client ID

1. In [dashboard.mx.com](https://dashboard.mx.com), generate a new API key pair.
2. Update `Mx__ApiKey` and `Mx__ClientId` in your secrets manager.
3. Perform a rolling restart of the `api` and `worker` containers (both use the MX
   client). Keep the old key active in MX dashboard until the new containers are
   confirmed healthy to avoid a gap in service.
4. Revoke the old key in MX dashboard.

### Webhook secret

1. In your MX dashboard, rotate the signing secret for your webhook endpoint.
2. Update `Mx__WebhookSecret` in your secrets manager.
3. Restart the `api` container so the new secret is loaded.
4. Verify that a test webhook payload from MX arrives and is accepted (HTTP 200) by
   checking your API logs.

Note: there is a brief window during the restart when incoming webhooks signed with the
new secret will be rejected. MX retries failed webhook deliveries, so events will not be
permanently lost.

## PostgreSQL security

- `POSTGRES_PASSWORD` has no default; compose refuses to start until you set a strong one.
- The compose file binds Postgres (and Redis, API, Web) to `127.0.0.1` only, for the local
  dev loop and migrate scripts. Keep it that way, or remove the `ports:` mapping entirely;
  never publish the database on a public interface.
- Restrict the `flowledger` user to the minimum required privileges on the `flowledger`
  database; do not use the PostgreSQL superuser in application connection strings.
- Enable TLS for the Postgres connection (Npgsql syntax: `SSL Mode=VerifyFull`) when the
  database is not on the same private network as the app. The compose connection string
  has no TLS option; see [database.md](database.md) for an override example, pool sizing,
  RPO/RTO and the major-version upgrade procedure.
- Enable SSL for the Postgres connection by adding `sslmode=require` to the connection
  string when the database is not on the same private network as the app.

## Data Protection key ring (web sign-in cookies, antiforgery)

The web app encrypts its sign-in cookie and antiforgery tokens with an ASP.NET Data Protection key ring. By default
that ring lives in the container's home directory and is lost on every recreate, which logs every user out; with
more than one web replica each would use a different ring and sign-in would break.

- Mount a persistent volume into the web container and set `DataProtection__KeysPath` to it (for example
  `/keys`, owned by the container user). All replicas must share it.
- Optional: `DataProtection__CertificatePath` (+ `DataProtection__CertificatePassword`) encrypts the key files at
  rest with a PFX. Without it the key files are plain XML, so restrict the volume to the container user.
- Set `DataProtection__Required=true` once the volume is mounted: outside Development the web app then refuses to
  start without a key path, instead of silently using an ephemeral ring.
- Include the key directory in the recovery set (back it up with the database; restoring the database without it
  only costs users a re-sign-in).
- Rotation: keys roll automatically every 90 days; old keys stay in the directory so existing cookies keep working.
  To force every session to end (suspected compromise), stop the web app, move the key files out of the directory,
  and start it again. Never delete keys while the app is running.
- Verification: sign in, run `docker compose up -d --force-recreate web`, reload: you must still be signed in.
  (Not yet verified end to end; compose does not mount the volume yet.)

## Checklist before going live

- [ ] `DataProtection__KeysPath` points at a persistent volume (and `DataProtection__Required=true`).

- [ ] `POSTGRES_PASSWORD` is a strong random value (not the default).
- [ ] `API__KEY` is a strong random value (not `dev-local-key-not-for-production`).
- [ ] Postgres port is not exposed to the public internet.
- [ ] TLS termination is in place; all HTTP redirects to HTTPS.
- [ ] `Mx__Enabled=false` (or credentials are set and verified if enabling MX).
- [ ] `Mx__BaseUrl=https://api.mx.com` (not the sandbox URL) if using live MX data.
- [ ] Secrets are stored in a secrets manager, not in `.env` on the server.
- [ ] OTLP telemetry is wired up for observability (`OTEL_EXPORTER_OTLP_ENDPOINT`).
- [ ] Backups are configured for the `postgres_data` volume.

## Multi-user hardening (secure defaults)

When OIDC sign-in is on, outside Development (full upgrade table and opt-outs: [upgrading.md](upgrading.md#upgrading-to-the-multi-user-secure-defaults-135-145-159)):

- **Signed tenant assertion (#135).** The shared `Api__Key` can act as any tenant unless the API also demands the Web app's signature over the
  tenant id. `Api:RequireSignedTenant` therefore defaults to true; startup fails unless `Api:TenantSigningKey` is set (same value on api and web,
  >= 32 characters, different from `Api:Key`). The api learns that OIDC is on from `Auth:Oidc:Authority`/`ClientId` (compose passes them).
- **Household tenant id (#135).** The household/admin tenant gates `/api/data/*` and billable provisioning, so with OIDC on it must not be the
  well-known `00000000-0000-0000-0000-000000000001`. A new install (empty database) needs no setting: the api generates a random id on its first
  start and stores it in the database (`system_settings`); the worker reads it, so start the api first. An existing install keeps its id (set
  `Api__TenantId` to it) or acknowledges the constant with `Tenancy__AllowWellKnownLegacyTenantId=true`. An explicit `Api__TenantId` always wins.
  Importing an export restores the source's id; restart api and worker afterwards.
- **Tenant signature v2 (#135).** The signature is bound to the HTTP method and path and carries a single-use nonce (bounded cache, 60-second
  window). `Api__AcceptLegacyTenantSignature` (default true) keeps the old format working during a rolling upgrade; upgrade the api first, then
  set it to false once the web is upgraded.
- **Sign-up (#145).** `Auth:SignUp:Mode` is `Open`, `InviteOnly` (default) or `Closed`. InviteOnly admits only logins whose verified email is on
  `Auth:Oidc:AllowedEmails` / `AllowedEmailDomains`; existing logins and the configured household owner always sign in. `Auth:SignUp:LegacyOpen=true`
  restores open sign-up (startup Warning). Prefer also restricting the application at the IdP. Email trust: leave `Tenancy:TrustUnverifiedEmail` off.
- **Sessions (#159).** `Auth:Session:AbsoluteMaxDays` (default 30) caps a session from sign-in; the cookie is `__Host-flowledger.auth` (it is
  always Secure outside Development, so terminate TLS at the proxy and set `ForwardedHeaders__Enabled=true` as in
  [Reverse proxy / TLS](#reverse-proxy--tls) so the app builds https redirect URIs). Unlinking a login ends its session within 5 minutes.

## Reverse proxy / TLS

If a proxy terminates TLS in front of the Web app (or the API), set `ForwardedHeaders__Enabled=true` **and** name the proxy: `ForwardedHeaders__KnownProxies=<ip>[,<ip>]` and/or `ForwardedHeaders__KnownNetworks=<cidr>[,<cidr>]` (the address the app sees, e.g. the compose bridge gateway `172.18.0.0/16`). The app refuses to start if forwarding is enabled without either, and headers from any other caller are ignored. `X-Forwarded-Host` is honoured only when `AllowedHosts` is restricted (e.g. `AllowedHosts=flowledger.example.com;localhost`); outside Development a startup warning is logged while `AllowedHosts` is `*`. Include `localhost` (or whatever your container health check uses) in `AllowedHosts`.

## Many tenants

Scheduled jobs visit tenants one at a time by default. With more than a handful of users set `Worker__MaxParallelTenants` (1-16, e.g. 4) on the worker to sync several tenants concurrently; keep it small so provider rate limits and the database connection pool hold.

## Session key ring (Data Protection)

The Web app persists its ASP.NET Data Protection key ring at `DataProtection__KeyPath` (compose mounts the
`web_keys` volume at `/keys`). With OIDC enabled outside Development the Web app refuses to start without it.
Include the volume in your backups; losing it only signs users out. See [key rotation](../runbooks/key-rotation.md).

## Single worker replica

Run exactly one Worker. Quartz uses the in-memory store, so a second replica would double-run scheduled
backup and alert jobs (per-tenant sync is protected by an advisory lock). Migrations are serialized by an
advisory lock, so concurrent API/Worker starts are safe.
