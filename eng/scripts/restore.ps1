<#
.SYNOPSIS  Restores a backup made by backup.ps1 into a SCRATCH database (never overwrites the live one).
.DESCRIPTION
  Creates <TargetDatabase> (default flowledger_restore_test), restores the dump into it, and prints
  row counts so you can compare against the live database. Restores the object archive to <ObjectsTarget>
  only if you pass it.
#>
param(
  [Parameter(Mandatory)][string]$BackupDir,
  [string]$PostgresContainer,
  [string]$TargetDatabase = 'flowledger_restore_test',
  [string]$ObjectsTarget
)
$ErrorActionPreference = 'Stop'
if ($TargetDatabase -eq 'flowledger') { throw "Refusing to restore over the live 'flowledger' database." }
if (-not $PostgresContainer) {
  $PostgresContainer = (docker ps --format '{{.Names}}' | Where-Object { $_ -like 'postgres*' } | Select-Object -First 1)
}
if (-not $PostgresContainer) { throw 'No running postgres container found.' }
$pw = docker exec $PostgresContainer printenv POSTGRES_PASSWORD
if (-not $pw) { throw 'Could not read POSTGRES_PASSWORD from the container.' }
function PgExec { docker exec -e "PGPASSWORD=$pw" $PostgresContainer @args }

docker cp (Join-Path $BackupDir 'flowledger.dump') "${PostgresContainer}:/tmp/restore.dump"
PgExec psql -U postgres -c "DROP DATABASE IF EXISTS $TargetDatabase;" | Out-Null
PgExec psql -U postgres -c "CREATE DATABASE $TargetDatabase;" | Out-Null
PgExec pg_restore -U postgres -d $TargetDatabase --no-owner /tmp/restore.dump
docker exec $PostgresContainer rm -f /tmp/restore.dump

foreach ($t in 'transactions','accounts','sync_cursors','provider_connections','account_balance_snapshots') {
  $n = PgExec psql -U postgres -d $TargetDatabase -tA -c "SELECT count(*) FROM $t;"
  Write-Host ("{0,-28} {1}" -f $t, $n.Trim())
}
if ($ObjectsTarget) {
  Copy-Item -Recurse -Force (Join-Path $BackupDir 'objects') $ObjectsTarget
  Write-Host "Objects restored -> $ObjectsTarget"
}
