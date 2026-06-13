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
