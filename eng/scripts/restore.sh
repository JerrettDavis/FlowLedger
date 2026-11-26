#!/usr/bin/env bash
# FlowLedger — Restore a backup made by backup.sh/backup.ps1 into a SCRATCH database.
#
# Linux/macOS twin of restore.ps1. Never overwrites the live 'flowledger' database: it creates
# TARGET_DATABASE (default flowledger_restore_test), restores the dump into it and prints row
# counts so you can compare against the live database. The object archive is restored to
# OBJECTS_TARGET only if you set it.
#
# Usage:
#   ./eng/scripts/restore.sh <backup-dir>
#   TARGET_DATABASE=scratch OBJECTS_TARGET=/srv/objects ./eng/scripts/restore.sh <backup-dir>
#
# Environment: POSTGRES_CONTAINER, TARGET_DATABASE, OBJECTS_TARGET (see backup.sh for container discovery)

set -euo pipefail

BACKUP_DIR="${1:-}"
[[ -n "$BACKUP_DIR" && -f "$BACKUP_DIR/flowledger.dump" ]] || {
  echo "Usage: $0 <backup-dir>   (a directory containing flowledger.dump)" >&2
  exit 1
}
TARGET_DATABASE="${TARGET_DATABASE:-flowledger_restore_test}"
OBJECTS_TARGET="${OBJECTS_TARGET:-}"
if [[ "$TARGET_DATABASE" == "flowledger" ]]; then
  echo "Refusing to restore over the live 'flowledger' database." >&2
  exit 1
fi
# The name is interpolated into SQL identifiers below; allow only plain identifiers.
[[ "$TARGET_DATABASE" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || { echo "Invalid TARGET_DATABASE." >&2; exit 1; }

find_postgres() {
  local c
  c="$(docker compose ps -q postgres 2>/dev/null | head -n1 || true)"
  if [[ -z "$c" ]]; then
    c="$(docker ps --format '{{.Names}}' | grep -E '(^|[-_])postgres' | head -n1 || true)"
  fi
  printf '%s' "$c"
}

PG="${POSTGRES_CONTAINER:-$(find_postgres)}"
[[ -n "$PG" ]] || { echo "No running postgres container found." >&2; exit 1; }

PGUSER_C="$(docker exec "$PG" printenv POSTGRES_USER 2>/dev/null || true)"
PGUSER_C="${PGUSER_C:-postgres}"
PGPASS_C="$(docker exec "$PG" printenv POSTGRES_PASSWORD 2>/dev/null || true)"
[[ -n "$PGPASS_C" ]] || { echo "Could not read POSTGRES_PASSWORD from the container." >&2; exit 1; }
pg() { docker exec -e "PGPASSWORD=$PGPASS_C" "$PG" "$@"; }

docker cp "$BACKUP_DIR/flowledger.dump" "$PG:/tmp/restore.dump"
pg psql -U "$PGUSER_C" -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS $TARGET_DATABASE;" >/dev/null
pg psql -U "$PGUSER_C" -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE $TARGET_DATABASE;" >/dev/null
pg pg_restore -U "$PGUSER_C" -d "$TARGET_DATABASE" --no-owner /tmp/restore.dump
docker exec "$PG" rm -f /tmp/restore.dump

for t in transactions accounts sync_cursors provider_connections account_balance_snapshots; do
  n="$(pg psql -U "$PGUSER_C" -d "$TARGET_DATABASE" -tA -c "SELECT count(*) FROM $t;")"
  printf '%-28s %s\n' "$t" "$n"
done

if [[ -n "$OBJECTS_TARGET" ]]; then
  mkdir -p "$OBJECTS_TARGET"
  cp -a "$BACKUP_DIR/objects/." "$OBJECTS_TARGET/"
  echo "Objects restored -> $OBJECTS_TARGET"
fi
