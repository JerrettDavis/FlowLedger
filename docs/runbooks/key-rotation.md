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

## `Secrets__EncryptionKey` (and legacy `HomeAssistant__EncryptionKey` / `Integrations__EncryptionKey`)

Stored webhook URLs and integration credentials are sealed with AES-256-GCM in a **v2 envelope**
(`v2:{keyId}:{base64}`): the key id is the first 4 bytes of SHA-256(key), and the tenant, purpose and (for integrations) row id are
bound in as authenticated data, so a value copied to another tenant or row fails to open. Older `v1:` values remain readable.

Key resolution (api and worker must agree):

- **Seal with** `Secrets__EncryptionKey`, else `Integrations__EncryptionKey` / `HomeAssistant__EncryptionKey` (the legacy names).
- **Open with** that key plus `Secrets__EncryptionKeyPrevious` (comma separated, unparsable entries are ignored) plus both legacy keys.

Rotation without re-entering anything:

1. Generate a new key (`openssl rand -base64 32`). Set `Secrets__EncryptionKey=<new>` and `Secrets__EncryptionKeyPrevious=<old>` on `api` and `worker`, restart.
   Every existing value still opens (v1 via the previous/legacy key, v2 by key id); new writes use the new key.
2. As the household admin, call `POST /api/data/reencrypt-secrets` (needs `X-Admin-Token` when `DataAdmin__Key` is set). It rewrites every
   value as v2 under the new key and reports `resealed`, `alreadyCurrent`, `unreadable`. It is idempotent; unreadable values are never overwritten.
3. When `unreadable` is 0, remove `Secrets__EncryptionKeyPrevious` (and the legacy keys) and restart.
4. If a key was compromised, also rotate the underlying secrets at their source: the old ciphertext is exposed.

Covered by `SecretBoxTests` (AAD binding, v1 compatibility, rotation, idempotent re-encryption).

## Restic password

Changing the repository password uses restic's own tooling (`restic key add` then `restic key remove`) against the repository,
with the same `B2_*` credentials. Do not remove the old key before the new one has restored a snapshot successfully. This
procedure is restic's, not FlowLedger's, and is not covered by FlowLedger tests. Losing every password makes the off-site backups unreadable.
