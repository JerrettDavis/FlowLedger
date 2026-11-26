<#
.SYNOPSIS
  Runs Stryker.NET mutation testing against FlowLedger.Domain (scoped, so it is quick).

.DESCRIPTION
  Stryker must run from the *test* project directory. This script restores the pinned tool (.config/dotnet-tools.json),
  then runs it with tests/mutation/stryker-config.domain.json. Reports land in tests/FlowLedger.Domain.Tests/StrykerOutput.

  -Target Application uses stryker-config.application.json (Forecasting, Transactions, RecurringFlows, Imports). It is
  much slower than Domain (Domain: about 1 minute) and has not been run to completion yet, so it has no baseline.

  Baseline: Domain scored 64.40 percent (see docs/development/testing.md); thresholds.break is 59 (baseline minus 5).
  Application's thresholds.break is 0 until a baseline is recorded.

.PARAMETER Target
  Domain (default) or Application.

.EXAMPLE
  pwsh tests/mutation/run-stryker.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Domain', 'Application')]
    [string] $Target = 'Domain'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$config = Join-Path $PSScriptRoot "stryker-config.$($Target.ToLowerInvariant()).json"
if (-not (Test-Path $config)) {
    throw "No Stryker config for '$Target' at $config (see the .DESCRIPTION of this script to add one)."
}

Push-Location $repo
try {
    dotnet tool restore
    Push-Location (Join-Path $repo "tests\FlowLedger.$Target.Tests")
    try {
        dotnet tool run dotnet-stryker --config-file $config
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally { Pop-Location }
}
finally { Pop-Location }
