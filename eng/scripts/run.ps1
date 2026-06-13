#!/usr/bin/env pwsh
# FlowLedger — One-Script Run (Windows / PowerShell)
# Usage: ./eng/scripts/run.ps1
# Prerequisites: .NET 10 SDK, Docker Desktop (or Podman)
#
# NOTE: On some Windows machines, if dotnet crashes with corrupted R2R images,
# set $env:DOTNET_ReadyToRun="0" before running this script.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host ">>> $msg" -ForegroundColor Cyan
}

# ── 1. Validate .NET SDK ─────────────────────────────────────────────────────
Write-Step "Validating .NET SDK..."
$sdkVersion = dotnet --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error ".NET SDK not found. Install .NET 10 from https://dot.net"
    exit 1
}
Write-Host "  SDK: $sdkVersion"
if (-not $sdkVersion.StartsWith("10.")) {
    Write-Warning "Expected .NET 10.x, got $sdkVersion. Continuing — check global.json."
}

# ── 2. No workload check needed ────────────────────────────────────────────────
# FlowLedger uses Aspire.AppHost.Sdk (NuGet SDK model) which does not require the legacy aspire workload.

# ── 3. Validate container runtime ────────────────────────────────────────────
Write-Step "Validating container runtime (Docker/Podman)..."
$docker = docker info 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker is not running. Start Docker Desktop and retry."
    exit 1
}
Write-Host "  Container runtime: OK"

# ── 4. Restore packages ───────────────────────────────────────────────────────
Write-Step "Restoring NuGet packages..."
dotnet restore

# ── 5. Build ──────────────────────────────────────────────────────────────────
Write-Step "Building solution..."
dotnet build --no-restore --configuration Debug

# ── 6. Launch AppHost (Aspire orchestrates Postgres, Redis, API, Web, Worker)
Write-Step "Starting FlowLedger via Aspire AppHost..."
Write-Host ""
Write-Host "  Aspire assigns dynamic ports via service discovery." -ForegroundColor Green
Write-Host "  Open the Aspire dashboard to see the actual resource URLs and login token:" -ForegroundColor Green
Write-Host "  https://localhost:15888" -ForegroundColor Green
Write-Host ""
Write-Host "  Press Ctrl+C to stop all services." -ForegroundColor DarkGray
Write-Host ""

Set-Location "$PSScriptRoot/../../src/FlowLedger.AppHost"
dotnet run --no-build
