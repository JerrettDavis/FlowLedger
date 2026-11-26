#!/usr/bin/env bash
# FlowLedger — Back up the database (pg_dump) AND the raw provider payload archive.
#
# Linux/macOS twin of backup.ps1. Output: <OUT_DIR>/<timestamp>/{flowledger.dump, objects/}.
# Nothing is ever deleted by this script. The last line printed is the backup directory.
#
# Usage:
#   ./eng/scripts/backup.sh
#   POSTGRES_CONTAINER=myproject-postgres-1 OUT_DIR=/srv/backups ./eng/scripts/backup.sh
#
# Environment:
#   POSTGRES_CONTAINER  postgres container (default: `docker compose ps -q postgres`, then the first
#                       running container whose name starts with or contains "postgres")
#   DATABASE            database to dump (default: flowledger)
#   OUT_DIR             backup root (default: $HOME/.flowledger/backups)
#   OBJECTS_DIR         raw archive directory on the host (default: $HOME/.flowledger/objects)
#   OBJECTS_CONTAINER   instead of OBJECTS_DIR, copy /data/objects out of this running container
#                       (compose: the api or worker container; the objects_data volume lives there)

set -euo pipefail

DATABASE="${DATABASE:-flowledger}"
OUT_DIR="${OUT_DIR:-$HOME/.flowledger/backups}"
OBJECTS_DIR="${OBJECTS_DIR:-$HOME/.flowledger/objects}"
OBJECTS_CONTAINER="${OBJECTS_CONTAINER:-}"

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

STAMP="$(date +%Y%m%d-%H%M%S)"
DEST="$OUT_DIR/$STAMP"
mkdir -p "$DEST"

pg pg_dump -U "$PGUSER_C" -d "$DATABASE" -Fc -f /tmp/flowledger.dump
docker cp "$PG:/tmp/flowledger.dump" "$DEST/flowledger.dump"
docker exec "$PG" rm -f /tmp/flowledger.dump

if [[ -n "$OBJECTS_CONTAINER" ]]; then
  docker cp "$OBJECTS_CONTAINER:/data/objects" "$DEST/objects"
elif [[ -d "$OBJECTS_DIR" ]]; then
  cp -a "$OBJECTS_DIR" "$DEST/objects"
fi

# Verify the dump is readable before declaring success.
docker cp "$DEST/flowledger.dump" "$PG:/tmp/verify.dump"
TOC="$(pg pg_restore -l /tmp/verify.dump)"
docker exec "$PG" rm -f /tmp/verify.dump
if ! grep -q 'transactions' <<<"$TOC"; then
  echo "Backup verification failed: dump has no transactions table entry." >&2
  exit 1
fi

echo "Backup OK -> $DEST" >&2
echo "$DEST"
