#!/usr/bin/env bash
# FlowLedger — Apply EF Core migrations against the compose database
#
# Usage:
#   ./eng/scripts/migrate.sh
#   FLOWLEDGER_DB="Host=localhost;..." ./eng/scripts/migrate.sh
#
# Prerequisites: .NET 10 SDK, dotnet-ef tool
# The compose postgres must be healthy before running this script.

set -euo pipefail

step() { echo ""; echo ">>> $1"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
INFRA_PROJECT="$REPO_ROOT/src/FlowLedger.Infrastructure"
STARTUP_PROJECT="$REPO_ROOT/src/FlowLedger.Api"

# ── Resolve connection string ─────────────────────────────────────────────────
if [ -z "${FLOWLEDGER_DB:-}" ]; then
    PG_PASSWORD="${POSTGRES_PASSWORD:-dev_only_change_in_prod}"
    export FLOWLEDGER_DB="Host=localhost;Port=5432;Database=flowledger;Username=flowledger;Password=$PG_PASSWORD"
    echo "  Using default dev connection string (set FLOWLEDGER_DB or POSTGRES_PASSWORD to override)"
fi

# ── Pre-migration backup (data-safety guard) ──────────────────────────────────
# A migration can destroy data, so take a pg_dump first and ABORT if it fails.
# Set SKIP_BACKUP=1 only for a brand-new empty database (the first ever migration).
if [ "${SKIP_BACKUP:-0}" != "1" ]; then
    step "Backing up the database before migrating..."
    BACKUP_DIR="$HOME/.flowledger/backups"
    mkdir -p "$BACKUP_DIR"
    DUMP="$BACKUP_DIR/premigrate-$(date +%Y%m%d-%H%M%S).dump"
    PG_USER="${POSTGRES_USER:-flowledger}"
    if ! docker compose exec -T postgres pg_dump -U "$PG_USER" -Fc flowledger > "$DUMP" || [ "$(wc -c < "$DUMP")" -lt 1024 ]; then
        echo "Pre-migration backup failed or is empty ($DUMP). Aborting; nothing was migrated." >&2
        echo "Use SKIP_BACKUP=1 only for a brand-new empty database." >&2
        exit 1
    fi
    echo "  Backup OK -> $DUMP"
fi

step "Applying EF Core migrations..."
echo "  Target: $(echo "$FLOWLEDGER_DB" | sed 's/Password=[^;]*/Password=***/')"

# ── Ensure dotnet-ef is available ─────────────────────────────────────────────
if ! dotnet ef --version >/dev/null 2>&1; then
    echo "  dotnet-ef not found — installing..."
    dotnet tool install --global dotnet-ef
fi

dotnet ef --version | head -1 | xargs -I{} echo "  dotnet-ef: {}"

# ── Run migrations ─────────────────────────────────────────────────────────────
dotnet ef database update \
    --project "$INFRA_PROJECT" \
    --startup-project "$STARTUP_PROJECT" \
    --no-build

echo ""
echo "  Migrations applied successfully."
