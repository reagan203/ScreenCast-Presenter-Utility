<#
.SYNOPSIS
  Signs the published exes with your code-signing certificate (Authenticode, SHA-256, timestamped).
.DESCRIPTION
  Needs signtool.exe (Windows SDK) and a code-signing certificate in your certificate store,
  e.g. on a hardware token. Pass its thumbprint. Timestamping contacts the timestamp server you
  give (the only network use in the whole build; the app itself never connects anywhere).
.EXAMPLE
  .\tools\sign.ps1 -Thumbprint 0123ABCD... -TimestampUrl http://timestamp.digicert.com
#>
param(
    [Parameter(Mandatory)] [string]$Thumbprint,
    [Parameter(Mandatory)] [string]$TimestampUrl,
    [string[]]$Runtimes = @('win-x64', 'win-arm64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found: install the Windows SDK.' }

foreach ($rid in $Runtimes) {
    $exe = Join-Path $root "artifacts\$rid\ScreenCastPresenter.exe"
    if (-not (Test-Path $exe)) { throw "$exe not found: run tools\publish.ps1 first." }
    & $signtool.FullName sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'ScreenCast Presenter' $exe
    if ($LASTEXITCODE -ne 0) { throw "Signing $rid failed." }
    & $signtool.FullName verify /pa $exe
    if ($LASTEXITCODE -ne 0) { throw "The signature on $rid didn't verify." }
}
