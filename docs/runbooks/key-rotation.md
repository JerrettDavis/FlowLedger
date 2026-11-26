# Runbook: key and secret rotation

Procedures that already exist are linked rather than repeated.

| Secret | Procedure |
|---|---|
| `API__KEY` | [production.md](../self-hosting/production.md#rotating-api__key-without-downtime): rotate on `api` and `web` together |
| `Mx__ApiKey`, `Mx__ClientId`, `Mx__WebhookSecret` | [production.md](../self-hosting/production.md#rotating-mx__apikey--mx__clientid--mx__webhooksecret) |
| `POSTGRES_PASSWORD` | Change the role password in Postgres (`ALTER ROLE flowledger PASSWORD '...'`), update `.env` / secrets manager, restart `api` and `worker`. Not exercised by tests. |
| OIDC client secret | Rotate in the IdP, update `Auth__Oidc__ClientSecret`, restart `web` |
| `Api__TenantSigningKey` | Set the same new value on `api` and `web` and restart both together |
| Restic repository password | See below |
| SimpleFIN access URL | Claim a new token with `scripts/simplefin-claim.ps1`, update `SimpleFin__AccessUrl`, restart `api` and `worker`, then disable the old one in the Bridge |
| Quiltt keys / webhook secret | Rotate in the Quiltt dashboard, update `Quiltt__ApiKeySecret` / `Quiltt__WebhookSecret`, restart `api` and `worker` |

## `HomeAssistant__EncryptionKey` and `Integrations__EncryptionKey`

These keys seal stored webhook URLs and integration credentials with AES-256-GCM. There is **no key versioning and no
re-encryption tool**. Values sealed with the old key cannot be opened with a new one (the app treats them as missing, it does not throw).
Rotating therefore means:

1. Generate a new key (`openssl rand -base64 32`) and set it on `api` and `worker` (same value on both).
2. Restart, then re-enter every Home Assistant webhook URL and integration credential/signing secret in the UI.
3. Assume values sealed under a compromised old key are exposed; rotate the underlying secrets at their source too.

If `Integrations__EncryptionKey` is unset it falls back to `HomeAssistant__EncryptionKey`, so rotating one may affect both.

## Restic password

Changing the repository password uses restic's own tooling (`restic key add` then `restic key remove`) against the repository,
with the same `B2_*` credentials. Do not remove the old key before the new one has restored a snapshot successfully. This
procedure is restic's, not FlowLedger's, and is not covered by FlowLedger tests. Losing every password makes the off-site backups unreadable.
