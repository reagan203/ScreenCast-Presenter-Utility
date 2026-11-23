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
