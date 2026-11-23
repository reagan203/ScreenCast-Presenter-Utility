<#
.SYNOPSIS
  Builds the release: one portable, self-contained ScreenCastPresenter.exe per architecture.
.DESCRIPTION
  Output: artifacts\win-x64\ScreenCastPresenter.exe and artifacts\win-arm64\ScreenCastPresenter.exe.
  Runs the unit tests first and stops if any fail. Sign the results with tools\sign.ps1.
.EXAMPLE
  .\tools\publish.ps1
  .\tools\publish.ps1 -Runtimes win-x64 -SkipTests
#>
param(
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $SkipTests) {
    dotnet test tests/ScreenCastPresenter.Tests -c Release -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed; nothing was published.' }
}

foreach ($rid in $Runtimes) {
    $out = Join-Path $root "artifacts\$rid"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish src/ScreenCastPresenter -c Release -r $rid -p:PublishSingleFile=true -o $out -nologo
    if ($LASTEXITCODE -ne 0) { throw "Publishing $rid failed." }
    $exe = Get-Item (Join-Path $out 'ScreenCastPresenter.exe')
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
    '{0,-10} {1,8:N1} MB  SHA-256 {2}' -f $rid, ($exe.Length / 1MB), $hash
}
