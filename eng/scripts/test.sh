#!/usr/bin/env bash
# FlowLedger — Test runner (Linux / macOS / WSL)
# Usage: ./eng/scripts/test.sh

set -euo pipefail
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

echo ">>> Format check (advisory)..."
dotnet format "$REPO_ROOT" --verify-no-changes --no-restore || true

echo ">>> Build..."
dotnet build "$REPO_ROOT" --no-restore --configuration Release

echo ">>> Unit & architecture tests..."
dotnet test "$REPO_ROOT" --no-build --configuration Release \
    --logger "console;verbosity=normal" \
    --collect:"XPlat Code Coverage"
