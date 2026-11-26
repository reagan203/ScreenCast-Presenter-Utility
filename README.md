# ScreenCast Presenter

A lightweight Windows tray utility that shows one chosen window, screen or region on a TV, a Zoom screen or a floating window, while you keep working privately on your main screen.

When you share a whole screen on Zoom or a TV, the audience sees everything: notifications, private windows, app switching. ScreenCast Presenter shows only the source you pick, so nothing else ever reaches the room or the call.

## Features

- **Present one thing:** any open window, a whole screen, a webcam or document camera, or a cropped region. Choose it from a list, by clicking it on screen, or from the command line.
- **Choose where it goes:** a TV or second monitor (fullscreen), a virtual "Zoom screen" to share in Zoom, Teams or Meet, or a floating window.
- **Swap live:** change what's shown with a smooth fade, from the tray icon or a keyboard shortcut. No white flashes.
- **Stay private:** a yellow outline on your screen marks what's being shown, and the TV never copies your whole desktop. Apps on your never-show list (password managers to start with) are never shown, and a whole screen turns black while one is open on it.
- **Help the room follow:** zoom in around your mouse, show your clicks, put a ring around the pointer, use a laser pointer, or draw on the TV. Add a small picture in a corner, like you on a webcam.
- **Run the session:** blank the TV with a countdown during a break, see how long you've been talking, play your favourites one after another on a lobby screen, and get an offer to show something when a TV is plugged in.
- **Show more than one thing:** split screen puts up to four windows, screens, cameras or pictures side by side, stacked, in a grid, or one large. Show a picture or a few lines of words (a "card") without opening an app.
- **Teach and demo:** your key combinations appear on the TV (never what you type), and zoom can follow where you type.
- **Hide what's private:** drag a box over part of a window (an inbox, a customer's details) and the audience sees a solid box or a blur there, even as you scroll. Screenshots and recordings of that window cover it too.
- **Stay in control:** a small private window shows exactly what the audience sees. Get the next thing ready where only you can see it, then show it with one key.
- **Point people at things:** a spotlight that dims everything but the area round your mouse, and arrows, boxes and text drawn on the TV.
- **Look the part:** your name and title along the bottom of the TV, your logo on the blank screen, and a QR code for a link, the Wi-Fi or your contact card, made on your PC with no internet.
- **Live captions:** what you say appears on the TV, worked out by Windows on your PC. Nothing is recorded or sent anywhere.
- **Scenes on one key:** save what's shown with everything around it (small picture, name, QR code, spotlight, live captions) and switch to it with Ctrl+Alt+F1 to F9 or a Stream Deck button.
- **Lobby screens:** playlists of favourites and scenes that take turns on a TV, in order or shuffled, started by a schedule or when the PC starts, carrying on after the TV is unplugged and back.
- **Check before you share:** see what's private in a window before it's shown, and cover it in one click. Password boxes are covered exactly where Windows says they are, and faces can be blurred.
- **Hide personal details automatically:** email addresses, phone and card numbers, IBANs, ID numbers, API keys and passwords, and your own list of names (clients, code names) are found on your PC as they appear and covered on the TV, in screenshots and in recordings.
- **Transcripts:** live captions can write down everything said, with subtitles (.srt and .vtt) to put under a recording. Captions come in three sizes, at the bottom or the top.
- **Phone remote:** pair your phone with a code only you see and use it as a clicker: next and previous slide, blank, freeze, zoom, your name banner, live captions, a break countdown, favourites, scenes and playlists. Off unless you switch it on; every command is signed, so others on the Wi-Fi can't take control.
- **Set up once:** save a room's setup (its TV, dimming, extras, favourites) and switch to it in one click. Schedule things, like starting the lobby slideshow at 8:00 and stopping it at 18:00. With three screens, show something different on a second TV.
- **Screenshots and recordings:** save what you present, with optional meeting and microphone audio, and chapters for each thing shown. Keep the last minute and save it when something goes well.
- **Run the room:** turn the TV on and off over the cable, follow the room's calendar file, say "Presenter, next slide", and keep private speaker notes on your screen and phone.
- **For IT teams:** rules set by Group Policy (never-show apps, always-on hiding, features switched off), and settings copied between PCs.
- **Small and fast:** about 15 MB of memory when idle and a few percent CPU while presenting.
- **Windows 11 look:** light and dark themes, the Windows accent colour, and a settings window laid out like Windows Settings.

## Command line

```powershell
ScreenCastPresenter.exe --present "PowerPoint" --output 2
ScreenCastPresenter.exe --favourite 3
ScreenCastPresenter.exe --blank
ScreenCastPresenter.exe --room "Board room"
ScreenCastPresenter.exe --qr "https://example.com/agenda"
ScreenCastPresenter.exe --action spotlight
ScreenCastPresenter.exe --scene 2
ScreenCastPresenter.exe --playlist "Lobby"
ScreenCastPresenter.exe --on spotlight --off auto-hide
ScreenCastPresenter.exe --split "Excel" --layout side-by-side
ScreenCastPresenter.exe --picture "C:\Lobby\welcome.png"
ScreenCastPresenter.exe --tv on --tv-input hdmi1
ScreenCastPresenter.exe --help
```

If it's already running, the command goes to the running copy; otherwise it starts. Exit code 0 means it worked, 1 that it couldn't be done (the reason is printed), and 2 a mistake in the command. Handy for button decks and desktop shortcuts.

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11. x64 and Arm64 are both supported.
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Optional, for a Zoom screen without a second monitor: [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver). The app can install it for you.

ScreenCast Presenter sends no data anywhere. It only goes online when you ask it to: to set up a Zoom screen, or, if you switch on "Check for updates", to ask GitHub once a day which version is the newest. Live captions use the microphone only while they're on, and Windows' own speech recognition for your language (Settings › Time & language › Speech). Automatic hiding uses Windows' own text recognition, on your PC. The phone remote is the only feature that listens on the network: it's off unless you switch it on, answers only on your local network, and runs in its own helper process.

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

The tests include pixel checks on the real GPU (hidden parts leave nothing showing through, even mid-fade; the QR code on the TV is read back and decoded) and speed budgets for everything done while presenting. See [docs/TESTING.md](docs/TESTING.md) for the desktop integration tests, the stress harness and the manual checks with a TV.

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
