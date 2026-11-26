#!/usr/bin/env bash
# FlowLedger - restore a backup made by backup.sh/backup.ps1 into a SCRATCH database
# (never overwrites the live one). Linux/macOS twin of restore.ps1.
#
# Usage: ./eng/scripts/restore.sh <backup-dir> [objects-target]
# Env:   POSTGRES_CONTAINER, TARGET_DATABASE (default flowledger_restore_test), PG_USER (default: the container's POSTGRES_USER, else postgres)
set -euo pipefail

BACKUP_DIR="${1:?usage: restore.sh <backup-dir> [objects-target]}"
OBJECTS_TARGET="${2:-}"
TARGET="${TARGET_DATABASE:-flowledger_restore_test}"
[ "$TARGET" != "flowledger" ] || { echo "Refusing to restore over the live 'flowledger' database." >&2; exit 1; }
CONTAINER="${POSTGRES_CONTAINER:-$(docker ps --format '{{.Names}}' | grep -i postgres | head -n1 || true)}"
[ -n "$CONTAINER" ] || { echo "No running postgres container found." >&2; exit 1; }

PG_USER="${PG_USER:-$(docker exec "$CONTAINER" printenv POSTGRES_USER 2>/dev/null || echo postgres)}"
PW="$(docker exec "$CONTAINER" printenv POSTGRES_PASSWORD)"
[ -n "$PW" ] || { echo "Could not read POSTGRES_PASSWORD from the container." >&2; exit 1; }
pg() { docker exec -e "PGPASSWORD=$PW" "$CONTAINER" "$@"; }

docker cp "$BACKUP_DIR/flowledger.dump" "$CONTAINER:/tmp/restore.dump"
pg psql -U "$PG_USER" -c "DROP DATABASE IF EXISTS $TARGET;" >/dev/null
pg psql -U "$PG_USER" -c "CREATE DATABASE $TARGET;" >/dev/null
pg pg_restore -U "$PG_USER" -d "$TARGET" --no-owner /tmp/restore.dump
docker exec "$CONTAINER" rm -f /tmp/restore.dump

for t in transactions accounts sync_cursors provider_connections account_balance_snapshots; do
  n="$(pg psql -U "$PG_USER" -d "$TARGET" -tA -c "SELECT count(*) FROM $t;")"
  printf '%-28s %s\n' "$t" "$(echo "$n" | tr -d '[:space:]')"
done
if [ -n "$OBJECTS_TARGET" ]; then
  mkdir -p "$OBJECTS_TARGET"
  cp -a "$BACKUP_DIR/objects/." "$OBJECTS_TARGET/"
  echo "Objects restored -> $OBJECTS_TARGET"
fi
