#!/usr/bin/env pwsh
# FlowLedger — Seed the demo household via the Simulated provider
#
# This script is IDEMPOTENT: calling it again on an already-seeded database is safe.
# Seeding = POST /api/connect (register provider accounts) then POST /api/sync
# (pull transactions from the Simulated data factory). The Simulated provider is
# deterministic, so repeated syncs produce the same transaction set.
#
# Guard: exits immediately if ASPNETCORE_ENVIRONMENT is not Development or if
# the --force flag is not passed — avoids accidental seeding in staging/prod.
#
# Usage:
#   ./eng/scripts/seed.ps1
#   ./eng/scripts/seed.ps1 -ApiBaseUrl "http://localhost:5001" -ApiKey "my-key"
#   ./eng/scripts/seed.ps1 -Force   # bypass environment guard

[CmdletBinding()]
param(
    [string]$ApiBaseUrl  = $env:FLOWLEDGER_API_URL ?? "http://localhost:5001",
    [string]$ApiKey      = $env:API__KEY ?? $env:Api__Key ?? "dev-local-key-not-for-production",
    [string]$TenantId    = "00000000-0000-0000-0000-000000000001",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host ">>> $msg" -ForegroundColor Cyan
}

# ── Environment guard ─────────────────────────────────────────────────────────
$aspnetEnv = $env:ASPNETCORE_ENVIRONMENT ?? "Production"
if ($aspnetEnv -notin @("Development", "Testing") -and -not $Force) {
    Write-Error "Seed is only allowed in Development/Testing environments. Pass -Force to override."
    exit 1
}

Write-Step "Seeding FlowLedger demo household (Simulated provider)..."
Write-Host "  API base:  $ApiBaseUrl" -ForegroundColor DarkGray
Write-Host "  Tenant:    $TenantId" -ForegroundColor DarkGray
Write-Host "  Key:       $('*' * $ApiKey.Length)" -ForegroundColor DarkGray

$headers = @{
    "X-Api-Key"    = $ApiKey
    "X-Tenant-Id"  = $TenantId
    "Content-Type" = "application/json"
}

# ── Wait for API to be healthy ────────────────────────────────────────────────
Write-Step "Waiting for API health check..."
$maxAttempts = 30
$attempt     = 0
do {
    $attempt++
    try {
        $health = Invoke-RestMethod -Uri "$ApiBaseUrl/alive" -Method Get -TimeoutSec 3 -ErrorAction SilentlyContinue
        Write-Host "  API is healthy." -ForegroundColor Green
        break
    }
    catch {
        if ($attempt -ge $maxAttempts) {
            Write-Error "API did not become healthy after $maxAttempts attempts. Is the stack running?"
            exit 1
        }
        Write-Host "  Waiting... ($attempt/$maxAttempts)" -ForegroundColor DarkGray
        Start-Sleep -Seconds 2
    }
} while ($true)

# ── Step 1: Connect (register Simulated provider accounts) ───────────────────
Write-Step "Step 1: Connecting Simulated provider..."
try {
    $connectResult = Invoke-RestMethod `
        -Uri         "$ApiBaseUrl/api/connect" `
        -Method      Post `
        -Headers     $headers `
        -ErrorAction Stop
    Write-Host "  Connected. Member ID: $($connectResult.memberId)  Provider: $($connectResult.provider)" -ForegroundColor Green
}
catch {
    if ($_.Exception.Response.StatusCode -eq 409) {
        Write-Host "  Already connected (409 Conflict). Continuing to sync." -ForegroundColor Yellow
    }
    else {
        Write-Error "Connect failed: $_"
        exit 1
    }
}

# ── Step 2: Sync (pull transactions from Simulated data factory) ──────────────
Write-Step "Step 2: Syncing demo transactions..."
$syncResult = Invoke-RestMethod `
    -Uri         "$ApiBaseUrl/api/sync" `
    -Method      Post `
    -Headers     $headers `
    -ErrorAction Stop

Write-Host "  Sync complete." -ForegroundColor Green
$syncResult | ConvertTo-Json -Depth 5 | Write-Host

Write-Host ""
Write-Host "  Demo household seeded successfully." -ForegroundColor Green
Write-Host "  Open http://localhost:5002 to view the dashboard." -ForegroundColor DarkGray
