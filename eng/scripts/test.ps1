#!/usr/bin/env pwsh
# FlowLedger — Test runner (Windows / PowerShell)
# Usage: ./eng/scripts/test.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$REPO_ROOT = Resolve-Path "$PSScriptRoot/../.."

Write-Host ">>> Format check (advisory)..." -ForegroundColor Cyan
dotnet format "$REPO_ROOT" --verify-no-changes --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Format issues detected. Run: dotnet format"
}

Write-Host ">>> Build..." -ForegroundColor Cyan
dotnet build "$REPO_ROOT" --no-restore --configuration Release

Write-Host ">>> Unit & architecture tests..." -ForegroundColor Cyan
dotnet test "$REPO_ROOT" --no-build --configuration Release `
    --logger "console;verbosity=normal" `
    --collect:"XPlat Code Coverage"
