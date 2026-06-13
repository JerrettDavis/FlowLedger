#!/usr/bin/env bash
# FlowLedger — Seed the demo household via the Simulated provider
#
# This script is IDEMPOTENT: calling it again on an already-seeded database is safe.
# Seeding = POST /api/connect then POST /api/sync using the Simulated provider.
#
# Guard: exits if ASPNETCORE_ENVIRONMENT is not Development unless --force is passed.
#
# Usage:
#   ./eng/scripts/seed.sh
#   FLOWLEDGER_API_URL=http://localhost:5001 ./eng/scripts/seed.sh
#   ./eng/scripts/seed.sh --force

set -euo pipefail

FORCE=false
for arg in "$@"; do
    [ "$arg" = "--force" ] && FORCE=true
done

step() { echo ""; echo ">>> $1"; }

API_BASE_URL="${FLOWLEDGER_API_URL:-http://localhost:5001}"
API_KEY="${API__KEY:-${Api__Key:-dev-local-key-not-for-production}}"
TENANT_ID="00000000-0000-0000-0000-000000000001"
ASPNET_ENV="${ASPNETCORE_ENVIRONMENT:-Production}"

# ── Environment guard ─────────────────────────────────────────────────────────
if [[ "$ASPNET_ENV" != "Development" && "$ASPNET_ENV" != "Testing" && "$FORCE" != "true" ]]; then
    echo "ERROR: Seed is only allowed in Development/Testing environments. Pass --force to override."
    exit 1
fi

step "Seeding FlowLedger demo household (Simulated provider)..."
echo "  API base:  $API_BASE_URL"
echo "  Tenant:    $TENANT_ID"

# ── Wait for API to be healthy ────────────────────────────────────────────────
step "Waiting for API health check..."
MAX_ATTEMPTS=30
ATTEMPT=0
until curl -sf "$API_BASE_URL/alive" >/dev/null 2>&1; do
    ATTEMPT=$((ATTEMPT + 1))
    if [ "$ATTEMPT" -ge "$MAX_ATTEMPTS" ]; then
        echo "ERROR: API did not become healthy after $MAX_ATTEMPTS attempts."
        exit 1
    fi
    echo "  Waiting... ($ATTEMPT/$MAX_ATTEMPTS)"
    sleep 2
done
echo "  API is healthy."

# ── Step 1: Connect ───────────────────────────────────────────────────────────
step "Step 1: Connecting Simulated provider..."
HTTP_STATUS=$(curl -s -o /tmp/fl_connect.json -w "%{http_code}" \
    -X POST "$API_BASE_URL/api/connect" \
    -H "X-Api-Key: $API_KEY" \
    -H "X-Tenant-Id: $TENANT_ID" \
    -H "Content-Type: application/json")

if [ "$HTTP_STATUS" = "200" ] || [ "$HTTP_STATUS" = "201" ]; then
    echo "  Connected: $(cat /tmp/fl_connect.json)"
elif [ "$HTTP_STATUS" = "409" ]; then
    echo "  Already connected (409). Continuing to sync."
else
    echo "ERROR: Connect failed (HTTP $HTTP_STATUS): $(cat /tmp/fl_connect.json)"
    exit 1
fi

# ── Step 2: Sync ──────────────────────────────────────────────────────────────
step "Step 2: Syncing demo transactions..."
curl -sf -X POST "$API_BASE_URL/api/sync" \
    -H "X-Api-Key: $API_KEY" \
    -H "X-Tenant-Id: $TENANT_ID" \
    -H "Content-Type: application/json" \
    | python3 -m json.tool 2>/dev/null || true

echo ""
echo "  Demo household seeded successfully."
echo "  Open http://localhost:5002 to view the dashboard."
