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
if ($Start) {
    # Start it through Explorer so it belongs to the desktop session, not to this script: a
    # process started by a script (or a terminal, or a tool) can be closed when that ends.
    Start-Process explorer.exe -ArgumentList "`"$target`""
    "Started ScreenCast Presenter."
}

# Windows 11 hides new tray icons behind the ^ arrow. Show this one on the taskbar (the same
# switch as Settings > Personalization > Taskbar > Other system tray icons). The entry appears
# once the icon has been shown, so wait a little for it.
$deadline = (Get-Date).AddSeconds(20)
$pinned = $false
while (-not $pinned -and (Get-Date) -lt $deadline) {
    foreach ($key in Get-ChildItem 'HKCU:\Control Panel\NotifyIconSettings' -ErrorAction SilentlyContinue) {
        if ((Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue).ExecutablePath -eq $target) {
            Set-ItemProperty $key.PSPath -Name IsPromoted -Value 1 -Type DWord
            $pinned = $true
        }
    }
    if (-not $pinned) { if ($Start) { Start-Sleep -Seconds 1 } else { break } }
}
if ($pinned) { "Tray icon set to always show on the taskbar." }
elseif ($Start) { "Couldn't find the tray icon entry yet; if it's hidden, open the ^ arrow by the clock and drag it onto the taskbar." }
