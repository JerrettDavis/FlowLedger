#!/usr/bin/env bash
# FlowLedger — database backup/restore drill against a running compose stack (intended for CI).
#
# 1. records the row count of every public table in the live database,
# 2. takes a backup with eng/scripts/backup.sh,
# 3. restores it into a scratch database with eng/scripts/restore.sh,
# 4. compares every table's row count and exits non-zero on any difference.
#
# This exercises the pg_dump/pg_restore path only. It does NOT exercise the in-app snapshot export/import,
# the restic off-site copy, or a wipe of the live database (restore.sh deliberately refuses to touch it).
#
# Usage (from the repo root, stack already up):
#   docker compose --profile full up -d --wait
#   ./scripts/dr-drill.sh

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DATABASE="${DATABASE:-flowledger}"
SCRATCH="${TARGET_DATABASE:-flowledger_drill}"
export TARGET_DATABASE="$SCRATCH"
# Work dir inside the repo checkout (not /tmp) so the path is valid for `docker cp` on every host,
# including Git Bash on Windows.
WORK="$(mktemp -d "$ROOT/.dr-drill.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

PG="${POSTGRES_CONTAINER:-$(docker compose ps -q postgres | head -n1)}"
[[ -n "$PG" ]] || { echo "No running postgres container (is the compose stack up?)." >&2; exit 1; }
export POSTGRES_CONTAINER="$PG"
USER_C="$(docker exec "$PG" printenv POSTGRES_USER || echo postgres)"
PASS_C="$(docker exec "$PG" printenv POSTGRES_PASSWORD)"
psqlc() { docker exec -e "PGPASSWORD=$PASS_C" "$PG" psql -U "$USER_C" -tA "$@"; }

counts() { # <database> -> "table count" lines, sorted
  local db="$1" t
  while read -r t; do
    [[ -n "$t" ]] || continue
    printf '%s %s\n' "$t" "$(psqlc -d "$db" -c "SELECT count(*) FROM \"$t\";")"
  done < <(psqlc -d "$db" -c "SELECT tablename FROM pg_tables WHERE schemaname='public' ORDER BY 1;") | sort
}

counts "$DATABASE" >"$WORK/live.txt"
[[ -s "$WORK/live.txt" ]] || { echo "Live database has no public tables; nothing to drill." >&2; exit 1; }

BACKUP_DIR="$(OUT_DIR="$WORK/backups" "$ROOT/eng/scripts/backup.sh" | tail -n1)"
"$ROOT/eng/scripts/restore.sh" "$BACKUP_DIR" >/dev/null

counts "$SCRATCH" >"$WORK/restored.txt"
psqlc -d postgres -c "DROP DATABASE IF EXISTS $SCRATCH;" >/dev/null

if diff -u "$WORK/live.txt" "$WORK/restored.txt"; then
  echo "DR drill OK: $(wc -l <"$WORK/live.txt") tables, all row counts match."
else
  echo "DR drill FAILED: restored row counts differ from live (diff above)." >&2
  exit 1
fi
