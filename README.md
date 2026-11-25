# ScreenCast Presenter

A lightweight Windows tray utility that shows one chosen window, screen or region on a TV, a Zoom screen or a floating window, while you keep working privately on your main screen.

When you share a whole screen on Zoom or a TV, the audience sees everything: notifications, private windows, app switching. ScreenCast Presenter shows only the source you pick, so nothing else ever reaches the room or the call.

## Features

- **Present one thing:** any open window, a whole screen, or a cropped region of either.
- **Choose where it goes:** a TV or second monitor (fullscreen), a virtual "Zoom screen" to share in Zoom, Teams or Meet, or a floating window.
- **Swap live:** change what's shown with a smooth fade, from the tray icon or a keyboard shortcut. No white flashes.
- **Stay private:** a yellow outline on your screen marks what's being shown, and the TV never copies your whole desktop.
- **Screenshots and recordings:** save what you present, with optional meeting and microphone audio.
- **Small and fast:** about 15 MB of memory when idle and a few percent CPU while presenting.
- **Windows 11 look:** light and dark themes, the Windows accent colour, and a settings window laid out like Windows Settings.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11. x64 and Arm64 are both supported.
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Optional, for a Zoom screen without a second monitor: [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver). The app can install it for you.

## Build and run

```powershell
dotnet build
dotnet run --project src/ScreenCastPresenter
```

To build portable, self-contained exes (no .NET install or admin rights needed to run them):

```powershell
.\tools\publish.ps1
```

The output goes to `artifacts\win-x64\` and `artifacts\win-arm64\`. See [docs/RELEASE.md](docs/RELEASE.md) for installing, signing and the release checklist.

## Tests

```powershell
dotnet test
```

See [docs/TESTING.md](docs/TESTING.md) for the desktop integration tests, the stress harness and the manual checks with a TV.

## Project layout

| Path | Contents |
| --- | --- |
| `src/ScreenCastPresenter` | The app: capture, output windows, recording, tray and UI |
| `tests/ScreenCastPresenter.Tests` | Unit and desktop integration tests |
| `tools/StressHarness` | Load, soak and flash tests |
| `tools/TestWindowHost` | Test windows used by the harness |
| `tools/*.ps1` | Publish, install, sign and icon scripts |
| `docs/SRS.md` | Requirements specification |

## Contributing

Issues and pull requests are welcome. Please run `dotnet test` before opening a pull request. The build treats warnings as errors.

## License

[MIT](LICENSE)
