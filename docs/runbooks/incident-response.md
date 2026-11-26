# Runbook: security incident

1. **Contain.** Stop exposure first: block the reverse proxy, or `docker compose stop web api worker`. Leave Postgres running only if you need it for forensics; it listens on loopback.
2. **Preserve.** Take a verified backup now (`eng/scripts/backup.sh` or `backup.ps1`) and copy the container logs (`docker compose logs --no-color > incident-logs.txt`) before anything is recreated.
3. **Scope.** Determine what was reachable: API key, OIDC sessions, provider credentials, the raw archive, backups. The Provider call ledger and request logs show which provider calls were made and when.
4. **Rotate.** Follow [key-rotation](key-rotation.md) for every secret in scope. Revoke provider access from the provider consoles (MX, Quiltt, SimpleFIN) and re-link afterwards.
5. **Recover.** Rebuild from known-good images, restore data from a backup that predates the compromise ([restore](restore.md)).
6. **Notify.** The operator is the data controller. Notify affected people and any authority as your jurisdiction requires; record what data classes were involved using the [data inventory](../privacy/data-inventory-and-retention.md).
7. **Review.** Write a short post-mortem; update the [threat model](../security/threat-model.md).

Vulnerability reports from outside arrive via GitHub private advisories (see SECURITY.md).
