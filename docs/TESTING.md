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
- **Session controller** (driven by fakes):
  - fades through black, blank and dim;
  - coalescing a thousand switches or blank presses;
  - a source closing mid-fade;
  - engine and watcher failures;
  - Stop cancelling queued work.

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
