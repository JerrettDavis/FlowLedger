#!/usr/bin/env pwsh
# FlowLedger — Apply EF Core migrations against the compose database
#
# Usage:
#   ./eng/scripts/migrate.ps1
#   ./eng/scripts/migrate.ps1 -ConnectionString "Host=localhost;Port=5432;Database=flowledger;Username=flowledger;Password=secret"
#
# Prerequisites: .NET 10 SDK, dotnet-ef tool (installed globally or via dotnet tool restore)
# The compose postgres must be healthy before running this script.

[CmdletBinding()]
param(
    [string]$ConnectionString = $env:FLOWLEDGER_DB
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host ">>> $msg" -ForegroundColor Cyan
}

# ── Resolve connection string ────────────────────────────────────────────────
if (-not $ConnectionString) {
    $pgPassword = $env:POSTGRES_PASSWORD ?? "dev_only_change_in_prod"
    $ConnectionString = "Host=localhost;Port=5432;Database=flowledger;Username=flowledger;Password=$pgPassword"
    Write-Host "  Using default dev connection string (set FLOWLEDGER_DB or POSTGRES_PASSWORD to override)" -ForegroundColor DarkGray
}

Write-Step "Applying EF Core migrations..."
Write-Host "  Target: $($ConnectionString -replace 'Password=[^;]+', 'Password=***')" -ForegroundColor DarkGray

# ── Ensure dotnet-ef is available ────────────────────────────────────────────
$efVersion = dotnet ef --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "  dotnet-ef not found — installing..." -ForegroundColor Yellow
    dotnet tool install --global dotnet-ef
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to install dotnet-ef. Run: dotnet tool install --global dotnet-ef"
        exit 1
    }
}
else {
    Write-Host "  dotnet-ef: $($efVersion | Select-Object -First 1)" -ForegroundColor DarkGray
}

# ── Run migrations ────────────────────────────────────────────────────────────
$repoRoot = Resolve-Path "$PSScriptRoot/../.."
$infraProject = Join-Path $repoRoot "src/FlowLedger.Infrastructure"
$startupProject = Join-Path $repoRoot "src/FlowLedger.Api"

$env:FLOWLEDGER_DB = $ConnectionString

dotnet ef database update `
    --project "$infraProject" `
    --startup-project "$startupProject" `
    --no-build

if ($LASTEXITCODE -ne 0) {
    Write-Error "Migration failed. Check that postgres is healthy and the connection string is correct."
    exit 1
}

Write-Host ""
Write-Host "  Migrations applied successfully." -ForegroundColor Green
