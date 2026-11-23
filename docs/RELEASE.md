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
