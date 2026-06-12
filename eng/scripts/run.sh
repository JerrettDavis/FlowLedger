#!/usr/bin/env bash
# FlowLedger — One-Script Run (Linux / macOS / WSL)
# Usage: ./eng/scripts/run.sh
# Prerequisites: .NET 10 SDK, Docker, Aspire workload

set -euo pipefail

step() { echo ""; echo ">>> $1"; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# ── 1. Validate .NET SDK ─────────────────────────────────────────────────────
step "Validating .NET SDK..."
sdk_version=$(dotnet --version 2>&1)
echo "  SDK: $sdk_version"
if [[ "$sdk_version" != 10.* ]]; then
    echo "WARNING: Expected .NET 10.x, got $sdk_version. Continuing — check global.json."
fi

# ── 2. Validate Aspire workload ───────────────────────────────────────────────
step "Validating Aspire workload..."
if ! dotnet workload list 2>&1 | grep -q aspire; then
    echo "  Installing Aspire workload..."
    dotnet workload install aspire
fi

# ── 3. Validate container runtime ────────────────────────────────────────────
step "Validating container runtime (Docker/Podman)..."
if ! docker info &>/dev/null; then
    echo "ERROR: Docker is not running. Start Docker and retry."
    exit 1
fi
echo "  Container runtime: OK"

# ── 4. Restore packages ───────────────────────────────────────────────────────
step "Restoring NuGet packages..."
dotnet restore "$REPO_ROOT"

# ── 5. Build ──────────────────────────────────────────────────────────────────
step "Building solution..."
dotnet build "$REPO_ROOT" --no-restore --configuration Debug

# ── 6. Launch AppHost ─────────────────────────────────────────────────────────
step "Starting FlowLedger via Aspire AppHost..."
echo ""
echo "  Aspire dashboard:  https://localhost:15888"
echo "  API:               https://localhost:5001"
echo "  Web:               https://localhost:5002"
echo ""
echo "  Press Ctrl+C to stop all services."
echo ""

cd "$REPO_ROOT/src/FlowLedger.AppHost"
dotnet run --no-build
