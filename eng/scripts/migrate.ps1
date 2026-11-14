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
    [string]$ConnectionString = $env:FLOWLEDGER_DB,
    [switch]$SkipBackup
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

# ── Pre-migration backup (data-safety guard) ─────────────────────────────────
# A migration can destroy data, so take a pg_dump first and ABORT if it fails. Pass -SkipBackup only
# for a brand-new empty database (the first ever migration).
if (-not $SkipBackup) {
    Write-Step "Backing up the database before migrating..."
    $backupDir = Join-Path $HOME ".flowledger/backups"
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $dump = Join-Path $backupDir ("premigrate-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".dump")
    $pgUser = $env:POSTGRES_USER ?? "flowledger"
    docker compose exec -T postgres pg_dump -U $pgUser -Fc flowledger | Set-Content -AsByteStream -Path $dump
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dump) -or (Get-Item $dump).Length -lt 1024) {
        Write-Error "Pre-migration backup failed or is empty ($dump). Aborting; nothing was migrated. Use -SkipBackup only for a brand-new empty database."
        exit 1
    }
    Write-Host "  Backup OK -> $dump" -ForegroundColor Green
}

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
