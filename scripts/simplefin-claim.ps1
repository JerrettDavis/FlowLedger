<#
.SYNOPSIS
  Claims a SimpleFIN setup token and switches the AppHost to REAL data via SimpleFIN Bridge.

.DESCRIPTION
  1. Prompts for the setup token (input hidden; tokens are single-use).
  2. Decodes it to the claim URL and POSTs to it, receiving the access URL (contains credentials).
  3. Stores the access URL in AppHost user-secrets (never printed, never written to the repo).
  4. Switches config for real data: SimpleFin:Enabled=true, Mx:Enabled=false, FlowLedger:DatabaseName=flowledger_prod
     (a fresh database - the data-environment guard refuses to mix real data with demo/sandbox rows).

  Restart the Aspire stack afterwards. Re-run with a new token if the access URL is ever revoked.
#>
param(
    [string]$AppHostProject = (Join-Path $PSScriptRoot '..\src\FlowLedger.AppHost'),
    [string]$DatabaseName = 'flowledger_prod'
)

$ErrorActionPreference = 'Stop'

$secure = Read-Host -Prompt 'SimpleFIN setup token' -AsSecureString
$token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)).Trim()

try {
    $claimUrl = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($token))
} catch {
    throw 'That does not look like a SimpleFIN setup token (not valid base64).'
}
if ($claimUrl -notmatch '^https://') { throw 'Decoded claim URL is not https; refusing.' }

$accessUrl = (Invoke-RestMethod -Method Post -Uri $claimUrl).ToString().Trim()
if ($accessUrl -notmatch '^https://[^:]+:[^@]+@') { throw 'Claim did not return an access URL (token already used or invalid).' }

Push-Location $AppHostProject
try {
    dotnet user-secrets set 'SimpleFin:AccessUrl' $accessUrl | Out-Null
    dotnet user-secrets set 'SimpleFin:Enabled' 'true' | Out-Null
    dotnet user-secrets set 'Mx:Enabled' 'false' | Out-Null
    dotnet user-secrets set 'FlowLedger:DatabaseName' $DatabaseName | Out-Null
} finally { Pop-Location }

Write-Host "Done. SimpleFIN access URL stored in AppHost user-secrets (not shown)."
Write-Host "Database -> $DatabaseName, MX disabled, SimpleFIN enabled. Restart the Aspire stack, then trigger a sync."
