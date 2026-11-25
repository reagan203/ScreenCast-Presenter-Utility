# Releasing ScreenCast Presenter

## Build

```
.\tools\publish.ps1
```

Runs the unit tests, then builds one portable `ScreenCastPresenter.exe` per architecture:

| Output | Size | Runs on |
| --- | --- | --- |
| `artifacts\win-x64\ScreenCastPresenter.exe` | ~129 MB | Windows 10 2004+ / 11, Intel and AMD |
| `artifacts\win-arm64\ScreenCastPresenter.exe` | ~141 MB | Windows 11 on Arm (Snapdragon) |

Each exe is self-contained: no .NET install and no admin rights needed. Copy it anywhere and run it. The size is the .NET runtime, WinForms and the Windows SDK projection. It is deliberately not compressed: a compressed bundle is unpacked into memory at every start, which would break the idle-memory budget (NFR-01). Uncompressed, the bundled code is memory-mapped from the exe and doesn't count as private memory.

Measured on the development laptop (x64 single-file build): idle at 15.3 MB private memory (budget 25 MB). The recording and capture helpers are the same exe started with a switch, and work the same from the single file.

## Icon

`tools\make-icon.ps1` draws the icon (a blue tile with a white screen and SCP's yellow outline around a window) at every Windows size into `src\ScreenCastPresenter\Assets\app.ico`. The 16, 20 and 24 px sizes are laid out on whole pixels so the tray and taskbar icons are sharp. The tray adds a green dot while presenting and a red one while recording.

## Install on this PC

For people who download the exe: running it shows "Add to the Start menu and desktop" in the first-run window (on by default) and in Settings › General. It installs to the same folder as the script below, with Start menu and desktop shortcuts and an entry in Settings › Apps for uninstalling. A newer download offers to replace the installed copy. Only a published single-file exe offers this, never a development build.

`.\tools\install.ps1` copies the exe to `%LOCALAPPDATA%\Programs\ScreenCast Presenter\` (no admin rights needed) and puts a "ScreenCast Presenter" shortcut on the desktop. Run it again to update: it waits until you quit SCP from the tray (it never closes it for you), and the shortcut, settings and start-with-Windows entry all stay. `-Start` starts it afterwards.

## Sign

```
.\tools\sign.ps1 -Thumbprint <your certificate's thumbprint> -TimestampUrl <your CA's timestamp URL>
```

This needs a code-signing certificate, which is the publisher's own (often on a hardware token), and `signtool.exe` from the Windows SDK. Unsigned builds work, but SmartScreen warns on first run.

## Before a release

1. `dotnet test` (all pass) and `SCP_LONG_TESTS=1 dotnet test --filter LongRecordingTests` (two-hour recording memory).
2. With the PC unlocked and nothing else running: `StressHarness.exe full`, then `StressHarness.exe soak --minutes 480` (8 hours, NFR-09).
3. The manual checks with a TV in docs/TESTING.md section 3.
4. `tools\publish.ps1`, `tools\sign.ps1`, then start each exe once to check it runs.

## Start with Windows

Settings › General › Start with Windows adds `"<exe path>" --startup` to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (no admin rights). Started that way, SCP waits quietly in the tray. If the exe is moved, SCP repairs the entry the next time it runs. If the user switches it off in Task Manager › Startup apps, SCP shows that rather than overriding it.
