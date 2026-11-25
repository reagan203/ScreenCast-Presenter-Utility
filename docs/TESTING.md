# Testing ScreenCast Presenter

There are three layers. Run the first after every change, the second before every commit, and the third before a release or after changes to presenting, fades or windows.

## 1. Unit tests (seconds)

```
dotnet test
```

Unit tests cover the logic only, with no windows or displays involved:

- **Settings:** corrupt, random-byte, huge, read-only and newer-version files.
- **Shortcuts:**
  - parsing combinations;
  - conflicts between two actions;
  - AltGr layouts that type characters with Ctrl+Alt;
  - pausing all shortcuts.
- **Favourites:** matching a saved window after its title changes.
- **Output and badge screens:** choosing the output screen, and choosing the presenter's screen.
- **Outline geometry:** maximised windows, windows spanning screens, never drawing on the TV.
- **Picker:** grid layout, keyboard movement, accent-insensitive search, cropping thumbnails while scrolling.
- **Recording:**
  - the audio mixer: summing, gain, running dry, clock drift over a long webinar, left/right never swapped;
  - the recording clock (pause), frame pacing (30 fps cap, a still picture repeated every second), the disk guard;
  - the helper protocol, including paths with accents, spaces and Japanese characters;
  - the recording controller (driven by a fake helper): progress, pause, stop, a crashed helper, a failed start, a disk too full to start, accidental one-second recordings;
  - one real encode on the GPU: three seconds of picture and tone, read back to check size, duration, frame count and both tracks.
- **Outputs (M5):** virtual displays named "Zoom screen", a real TV chosen before a virtual display, Ctrl+Alt+O cycling, the default and restored floating-window positions, settings round trip and repair; real output windows created on the desktop to check the styles Zoom and privacy rely on (floating: sizable, in the taskbar, not a tool window, not topmost, not excluded from capture; fullscreen: out of Alt+Tab, topmost); the ✕ on the floating window stops showing.
- **Start with Windows:** enabling, disabling, Task Manager switching it off, repairing the entry after the exe moves (on a throwaway registry key, never the real Run key).
- **Settings migration:** light became the default theme; version-1 files on the old default move to it once, real choices are kept.
- **Easier to use (rev 3):**
  - the tray menu in every state: short when idle, presenting and recording controls only when they apply, "Show again" after stopping, favourites only once there are some, one tick in Show on, every line runs a real command and has no jargon;
  - message timing (5–15 s by length and buttons), text size steps and repair, help topics (plain questions, each runs something, found by everyday words like "projector" or "videos");
  - no action name says "source", "output", "picker" or "palette"; screens are "Screen N"; no string in the app says "SCP" (a scan of the source);
  - Zoom screen setup: winget's exit codes, the exact command, no winget;
  - Start menu install in temporary folders and a throwaway registry key: paths with accents, Japanese and spaces, a locked installed copy, replacing a different build, start-with-Windows following the copy, uninstall keeping other files, a real shell shortcut;
  - keeping the tray icon visible (a throwaway key): no entry, hidden, pinned, other apps' entries untouched;
  - the confirmation dialog is hidden from capture, created cloaked and keyboard friendly.
- **Modern look (rev 4):**
  - the quick-actions panel in every state: what leads, toggles showing their state and keeping it open, red stop-recording tile, every tile runs a real command, has an icon, plain full words and fits;
  - every tray-menu and panel command has an icon, and every icon exists in Windows' icon font (checked with GDI's glyph lookup);
  - Windows accent colours from Windows blue to yellow, white, black and near-page colours stay readable on both themes (4.5:1 text, 3:1 against the page), a readable accent is used unchanged, and the outline stays yellow; the accent palette and DWM value are read correctly;
  - the Windows 11 type ramp, semibold from the font family rather than synthetic bold.
- **Session controller** (driven by fakes):
  - fades through black, blank and dim;
  - coalescing a thousand switches or blank presses;
  - a source closing mid-fade;
  - engine and watcher failures;
  - Stop cancelling queued work.

### Long tests (opt-in)

```
set SCP_LONG_TESTS=1
dotnet test --filter LongRecordingTests
```

Simulates a two-hour recording (1280×720 at 30 fps with sound) as fast as the GPU allows, about 9 minutes, and fails if memory grows more than 10 MB after warm-up (NFR-09a). It found a leak of ~70 MB per two hours that no short test showed: every COM object passed through .NET's built-in interop leaves ~200 bytes behind, so the recording hot paths use raw COM calls (`Interop/RawCom.cs`).

## 2. Stress harness (minutes; uses your desktop)

```
dotnet build -c Release
tools\StressHarness\bin\Release\net10.0-windows10.0.19041.0\StressHarness.exe quick
```

The harness starts the real app with a throwaway settings file, so your own settings are never touched. It opens its own test windows using `tools/TestWindowHost`, and drives the app only through its global shortcuts, as a user would.

The harness opens windows, presses keys and covers the screen. **Run it when you're not using the PC, and close ScreenCast Presenter first.**

| Scenario | What it does | Pass when |
| --- | --- | --- |
| `smoke` | Favourites, blank, blank with message, dim, previous source, stop | Every step works and the output closes on Stop |
| `picker` | Opens and closes the picker 20 times | Median open time ≤ 200 ms (NFR-08); picker, backdrop and thumbnails hidden from capture |
| `ui` | Opens and closes the command palette and the shortcuts page | Both open; privacy audit clean |
| `switches` | 200 full switches | No growth in memory, handles, GDI or USER objects |
| `storm` | 2,000 random shortcut presses with no pause | No backlog: Stop takes effect within 1 s |
| `flood` | 300 open windows, then cycles through them and opens the picker | Each cycle < 1.5 s; UI stays responsive |
| `hostile` | Constantly resizing, retitling, 4K, tiny and frozen sources; source closed mid-fade 50–1,000 times | No crash; SCP responsive while the source is frozen |
| `pressure` | Every CPU core at 100% while switching | UI round trip < 500 ms |
| `flash` | Records the output screen at display rate during 100 switches between two dark windows | Zero frames with more than 10% of pixels brighter than #303030 (NFR-16) |
| `record` | Records what the TV shows through a switch, blank and pause; stops presenting during a second TV recording; records a window in the background and closes it | Three playable files; the TV recording ends with the presentation; the window recording stops by itself; no recorder left running |
| `window` | Ctrl+Alt+O until the output is a floating window, a source switch, then the window's ✕ | The window is reused across switches; closing it stops showing; back to fullscreen afterwards |
| `widget` | Presents a window, clicks the now-showing bar with another window in front, toggles it with Ctrl+Alt+N, stops | The bar is off the TV and hidden from capture; the click brings the presented window to the front; it hides, returns, and goes away on Stop |
| `soak --minutes N` | A switch every 2 s and a blank every 20 s, sampled every 10 s | Memory drift < 5 MB (NFR-09) |
| `quick` / `full` | Every scenario above, at small or large counts | All pass |

Every probe checks:
- SCP is still running;
- its UI thread answers within the budget;
- every visible SCP window except the output is hidden from capture;
- at most one output window exists.

Samples go to `stress-*.csv` next to the harness exe.

For an 8-hour soak (SRS 8.1), run `StressHarness.exe soak --minutes 480`.

## 3. Manual checks (with the TV)

These need a person: how things look, the physical TV, Zoom, sleep, and unplugging the cable. Each milestone's handover lists them, and the SRS acceptance table (section 8.1) is the reference.

For revision 3 (easier to use), also:
- First run on a fresh Windows 11 user: step 2's drawing matches where the icon really is; with "Keep the icon next to the clock" on, the icon is on the taskbar afterwards; "Show something now" opens the picker.
- The published exe run from Downloads: "Add to the Start menu" adds Start and desktop shortcuts and a Settings › Apps entry; Settings › Apps › Uninstall closes the running copy (finishing a recording first) and removes the exe and shortcuts.
- "Set up a Zoom screen" on a PC without the driver: Windows' permission prompt, then a Zoom screen appears and is chosen; saying No to the prompt reports "cancelled". Check what the new screen does to window placement and the mouse.
- The share guide's steps against current Zoom and Teams.
- Text size Larger: picker cards, palette, Settings, first run, tray menu and messages have nothing cut off.

For revision 4 (modern look), also:
- On Windows 11 22H2+: Mica behind the title bars and the Settings section list, acrylic behind the quick-actions panel, in light and dark, over a busy wallpaper; text on them crisp and readable. On Windows 10: solid colours, icons from Segoe MDL2 Assets.
- High Contrast on and off while Settings and the panel are open: the backdrops come off and back.
- Animation effects off (Settings › Accessibility › Visual effects): no slide or fade anywhere.
- The quick-actions panel with the taskbar at the bottom, top, left and right; on a two-screen setup it opens on the screen you clicked, never on the TV; a second click on the icon closes it.
- "My Windows colour" with a few accents, including a very light and a very dark one.
