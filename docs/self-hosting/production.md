# Production Hardening & Secrets

This guide covers what you must do before exposing a FlowLedger instance to real users
or real financial data. Read it before switching `Mx__Enabled` to `true` in a non-dev
environment.

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
- Terminates TLS before forwarding to the container on port 5001 (API) and 5002 (Web).

## Rotating `API__KEY` without downtime

`API__KEY` is the bearer token shared between the Web frontend and the API. To rotate it:

1. Generate a new key: `openssl rand -hex 32`
2. Update the secret in your secrets manager / deployment platform.
3. Perform a rolling restart of the `api` and `web` containers so both pick up the new
   value simultaneously. (They must match — a Web container with the old key will get
   401 errors from an API container with the new key.)

   With compose:
   ```bash
   docker compose --profile full up --build -d --no-deps api web
   ```

   With Kubernetes: update the Secret, then roll the Deployments.

4. Verify the Web UI loads correctly after the restart.

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

- Change the default `POSTGRES_PASSWORD` before first deployment.
- The compose service exposes Postgres on host port 5432 for developer convenience.
  In production, remove the `ports:` mapping from the `postgres` service (or bind to
  `127.0.0.1:5432` only) so the database is not reachable from the public network.
- Restrict the `flowledger` user to the minimum required privileges on the `flowledger`
  database; do not use the PostgreSQL superuser in application connection strings.
- Enable SSL for the Postgres connection by adding `sslmode=require` to the connection
  string when the database is not on the same private network as the app.

## Checklist before going live

- [ ] `POSTGRES_PASSWORD` is a strong random value (not the default).
- [ ] `API__KEY` is a strong random value (not `dev-local-key-not-for-production`).
- [ ] Postgres port is not exposed to the public internet.
- [ ] TLS termination is in place; all HTTP redirects to HTTPS.
- [ ] `Mx__Enabled=false` (or credentials are set and verified if enabling MX).
- [ ] `Mx__BaseUrl=https://api.mx.com` (not the sandbox URL) if using live MX data.
- [ ] Secrets are stored in a secrets manager, not in `.env` on the server.
- [ ] OTLP telemetry is wired up for observability (`OTEL_EXPORTER_OTLP_ENDPOINT`).
- [ ] Backups are configured for the `postgres_data` volume.
