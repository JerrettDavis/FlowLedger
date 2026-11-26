#!/usr/bin/env bash
# FlowLedger - back up the database (pg_dump) AND the raw provider payload archive.
# Linux/macOS twin of backup.ps1. Nothing is ever deleted by this script.
#
# Usage: ./eng/scripts/backup.sh
# Env:   POSTGRES_CONTAINER (default: first running container whose name contains "postgres")
#        OBJECTS_DIR (default ~/.flowledger/objects)  OUT_DIR (default ~/.flowledger/backups)
#        DATABASE (default flowledger)  PG_USER (default: the container's POSTGRES_USER, else postgres)
set -euo pipefail

OBJECTS_DIR="${OBJECTS_DIR:-$HOME/.flowledger/objects}"
OUT_DIR="${OUT_DIR:-$HOME/.flowledger/backups}"
DATABASE="${DATABASE:-flowledger}"
CONTAINER="${POSTGRES_CONTAINER:-$(docker ps --format '{{.Names}}' | grep -i postgres | head -n1 || true)}"
[ -n "$CONTAINER" ] || { echo "No running postgres container found." >&2; exit 1; }

PG_USER="${PG_USER:-$(docker exec "$CONTAINER" printenv POSTGRES_USER 2>/dev/null || echo postgres)}"
PW="$(docker exec "$CONTAINER" printenv POSTGRES_PASSWORD)"
[ -n "$PW" ] || { echo "Could not read POSTGRES_PASSWORD from the container." >&2; exit 1; }
pg() { docker exec -e "PGPASSWORD=$PW" "$CONTAINER" "$@"; }

STAMP="$(date +%Y%m%d-%H%M%S)"
DEST="$OUT_DIR/$STAMP"
mkdir -p "$DEST"

pg pg_dump -U "$PG_USER" -d "$DATABASE" -Fc -f /tmp/flowledger.dump
docker cp "$CONTAINER:/tmp/flowledger.dump" "$DEST/flowledger.dump"
docker exec "$CONTAINER" rm -f /tmp/flowledger.dump

if [ -d "$OBJECTS_DIR" ]; then cp -a "$OBJECTS_DIR" "$DEST/objects"; fi

# Verify the dump is readable before declaring success.
docker cp "$DEST/flowledger.dump" "$CONTAINER:/tmp/verify.dump"
TOC="$(pg pg_restore -l /tmp/verify.dump)"
docker exec "$CONTAINER" rm -f /tmp/verify.dump
echo "$TOC" | grep -q 'transactions' || { echo "Backup verification failed: dump has no transactions table entry." >&2; exit 1; }

echo "Backup OK -> $DEST"
