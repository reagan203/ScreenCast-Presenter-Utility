<#
.SYNOPSIS
  Installs (or updates) ScreenCast Presenter for the current user and puts a shortcut on the desktop.
.DESCRIPTION
  Copies artifacts\win-x64\ScreenCastPresenter.exe (from tools\publish.ps1) to
  %LOCALAPPDATA%\Programs\ScreenCast Presenter\ and creates "ScreenCast Presenter" on the desktop.
  No admin rights. Settings, the shortcut and "start with Windows" are kept on update.
  If SCP is running, it asks you to quit it from the tray first (it never closes it for you:
  you might be presenting).
.EXAMPLE
  .\tools\install.ps1
#>
param([string]$Runtime = 'win-x64', [switch]$Start)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "artifacts\$Runtime\ScreenCastPresenter.exe"
if (-not (Test-Path $source)) { throw "Build it first: .\tools\publish.ps1 -Runtimes $Runtime" }

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ScreenCast Presenter'
$target = Join-Path $installDir 'ScreenCastPresenter.exe'
while (Get-Process ScreenCastPresenter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target }) {
    Read-Host 'ScreenCast Presenter is running. Quit it from its tray icon (right-click > Quit), then press Enter'
}

New-Item -ItemType Directory -Force $installDir | Out-Null
Copy-Item $source $target -Force

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'ScreenCast Presenter.lnk'))
$link.TargetPath = $target
$link.WorkingDirectory = $installDir
$link.IconLocation = "$target,0"
$link.Description = 'Show one window, screen or region on a TV, a Zoom screen or a window, privately.'
$link.Save()
"Installed $target and the desktop shortcut."
if ($Start) { Start-Process $target }
