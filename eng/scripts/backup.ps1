<#
.SYNOPSIS  Backs up the FlowLedger database (pg_dump) AND the raw provider payload archive.
.DESCRIPTION
  Real provider data is billable. Run this before and after every live sync session.
  Output: <OutDir>/<timestamp>/{flowledger.dump, objects/}. Nothing is ever deleted by this script.
.PARAMETER PostgresContainer  Container name/id (default: first running container whose name starts with 'postgres').
.PARAMETER ObjectsDir         Raw archive dir (default: ~/.flowledger/objects, matching AppHost).
.PARAMETER OutDir             Backup root (default: ~/.flowledger/backups).
#>
param(
  [string]$PostgresContainer,
  [string]$ObjectsDir = (Join-Path $HOME '.flowledger/objects'),
  [string]$OutDir = (Join-Path $HOME '.flowledger/backups'),
  [string]$Database = 'flowledger'
)
$ErrorActionPreference = 'Stop'
if (-not $PostgresContainer) {
  $PostgresContainer = (docker ps --format '{{.Names}}' | Where-Object { $_ -like 'postgres*' } | Select-Object -First 1)
}
if (-not $PostgresContainer) { throw 'No running postgres container found.' }
$pw = docker exec $PostgresContainer printenv POSTGRES_PASSWORD
if (-not $pw) { throw 'Could not read POSTGRES_PASSWORD from the container.' }
function PgExec { docker exec -e "PGPASSWORD=$pw" $PostgresContainer @args }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$dest = Join-Path $OutDir $stamp
New-Item -ItemType Directory -Force -Path $dest | Out-Null

PgExec pg_dump -U postgres -d $Database -Fc -f /tmp/flowledger.dump
if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
docker cp "${PostgresContainer}:/tmp/flowledger.dump" (Join-Path $dest 'flowledger.dump')
if ($LASTEXITCODE -ne 0) { throw 'docker cp failed' }
docker exec $PostgresContainer rm -f /tmp/flowledger.dump

if (Test-Path $ObjectsDir) { Copy-Item -Recurse -Force $ObjectsDir (Join-Path $dest 'objects') }

# Verify the dump is readable before declaring success.
docker cp (Join-Path $dest 'flowledger.dump') "${PostgresContainer}:/tmp/verify.dump" | Out-Null
$toc = PgExec pg_restore -l /tmp/verify.dump
docker exec $PostgresContainer rm -f /tmp/verify.dump
if (-not ($toc -match 'transactions')) { throw 'Backup verification failed: dump has no transactions table entry.' }

Write-Host "Backup OK -> $dest"
$dest
