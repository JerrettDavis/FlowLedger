# Runbook: key and secret rotation

| Secret | Where set | Impact of rotating | Procedure |
|---|---|---|---|
| `POSTGRES_PASSWORD` | `.env`, Postgres role | Brief outage | `ALTER ROLE flowledger PASSWORD '...'` via `docker compose exec postgres psql`, update `.env`, `docker compose up -d api worker` |
| `API__KEY` | `.env` (api and web read the same value) | Brief outage | Generate `openssl rand -hex 32`, update `.env`, `docker compose up -d api web worker` |
| OIDC client secret | IdP + `AUTH__OIDC__CLIENTSECRET` | Users re-login | Create the new secret at the IdP, update `.env`, recreate `web`, delete the old secret |
| Data Protection key ring | `web_keys` volume (`/keys`) | Keys roll automatically every 90 days; old keys stay for decrypting old cookies | Normal rotation needs no action. To force-rotate (suspected leak): stop `web`, remove the volume contents, start `web`; all users must sign in again. Always include the volume in backups |
| `HomeAssistant__EncryptionKey` | `.env` | Stored HA URLs become undecryptable | Re-enter the HA webhook URL in the app after changing the key. There is no automatic re-encryption yet |
| `Mx__*`, Quiltt, SimpleFIN | Provider consoles + `.env` | Syncs fail until updated | Rotate at the provider, update `.env`, recreate `api worker`; SimpleFIN access URLs are re-claimed with a new setup token |
| restic password | `Restic:PasswordFile` | Cannot read old snapshots with the new password | Add a new key with `restic key add`, verify, then `restic key remove` the old one. Keep a copy off the host |

After any rotation run a manual backup and a restore drill ([restore](restore.md)) to confirm the new
configuration works end to end.
