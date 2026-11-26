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
- **More for presenters (rev 5):**
  - click a window to show it: the front window under the pointer wins, the taskbar or a toolbar in front blocks the window behind, screens left of the main one;
  - the command line: every option and mistake (exit codes 0, 1, 2), the exe's own switches never taken as requests, window matching by exact app name then title (accents ignored), arguments with quotes and Japanese surviving the pipe, a real named pipe (on a test-only name, so a running copy is never reached), a silent client not blocking the next;
  - the never-show list: matching by program name, only the part of the screen shown counting, windows straddling screens, tidying a hand-edited list, password managers added for older settings files; the session's independent reasons to hold the picture black (nothing private ever seen even for a fade, blank and dim never revealing it);
  - the Do not disturb reminder's rules;
  - zoom: views centred, stopped at edges, never past the crop, never below the smallest crop, levels, easing; following without a fade; zooming through black; the view kept through freezing and a fallback engine;
  - click rings and the laser: swapped mouse buttons, a held button is one click, ring and trail timing, marks landing on the same spot through crop and letterbox; every mark's protocol line and garbled lines ignored;
  - the countdown and talk timer: clock text, two-line message, warnings once each, a clock change backwards; blanking with a message and changing it without a fade;
  - cameras: ids with backslashes, # and & reaching the helper intact, picture-size choice (Full HD, 15 fps or more, 30 preferred, portrait), favourites by id, a camera that can't start explaining why; asking Windows for cameras never throws;
  - the slideshow's order and skipping, screen arrivals (a flickering cable, a reused screen number);
  - drawing: the layer as a real (unshown) window: hidden from capture, catching the pen, a drag becoming the right points on the TV, tools, undo, clear, the layer following the window;
  - the small picture's layout, corners and sizes, and its protocol line;
  - the update check: version tags (betas and odd tags refused, also as links), skipping, the once-a-day rule, GitHub's answer read carefully;
  - the new tray submenu, panel tiles and help topics;
  - restarting the engine to apply a changed way of showing at once (blank, holds, zoom and message carried over);
  - **on the real GPU**: the capture engine draws every mark, the small picture and the two-line message into a hidden output window, and reports a small picture that can't start without failing. (It captures the main screen for about a second; nothing is saved.)
- **Stage tools (rev 6):**
  - hidden parts: kept per app and per screen, never for a camera, capped at 12, tidied from a hand-edited file, saved and loaded, a look from a newer version falling back to solid; the session covering them before any fade-in on every engine (a switch, a restart, freezing, a fallback) and refusing to show with an engine or fallback that can't cover them; the new source known when an engine takes over; screenshots and recordings covering them where they fall in the picture, clipped by the crop;
  - **on the real GPU, pixel by pixel** (drawn offscreen and read back): a solid hidden part identical over a white and a red picture at full brightness and mid-fade (nothing shows through), a blurred one smooth inside and sharp outside, both kept while frozen; the spotlight lit at the pointer and dimmed in the corner, and gone while frozen; arrows, boxes and text drawn where put and removed; a highlighter crossing itself not darker at the crossing, while being drawn and once finished; the banner and captions with the picture, never on plain black, and captions never on a break screen; **the QR code read back from the GPU's own picture and decoded**;
  - QR codes: Reed–Solomon against the standard's worked examples, format and version bits against its tables, every version 1–10 and most masks decoded by an independent decoder written from the standard (finders, timing, format copies, version blocks, Reed–Solomon syndromes of every block), UTF-8 text, the 213-byte limit, Wi-Fi escaping, the label naming the network and never the password, the form's checks;
  - arrows, boxes and text: a shape keeping its start while its end follows the drag, one tidy line of text, a shape only clicked taken away, the drawing toolbar's keys, the text box hidden from capture;
  - layout on the TV: the banner as a lower third as wide as its words, captions above it, a bottom QR card above both, whole-pixel QR modules, the logo fitted and never blown up, the message under it; logo files checked (not a picture, too big, missing, wrong type), loaded unlocked and scaled down;
  - rooms: made from the current setup and applied back (copies of favourites, the second TV never the first's screen), tidied (names, duplicates, limits), saved and loaded; schedules: times and days read and described, entries tidied, due once when their time passes, only on their days, skipped when missed by more than 5 minutes, never repeated when the clock goes back, across midnight in order, the next time within a week, every action offered in the form;
  - live captions: guesses replaced by what was said, the last sentences kept, silence clearing them, junk ignored, a cut at a whole word, plain reasons when listening fails; the helper itself was also run for real on this PC (`ScreenCastPresenter.exe --captions`);
  - every new helper command (hidden parts, spotlight, banner, logo, QR, live captions, a shape's end, a text mark) sent as the app sends it and read by **the helper's own parser** into the same call; nonsense lines ignored and never putting anything on the TV;
  - the command line's `--room` and `--qr`, every new action by name, the captions helper's switch never a request;
  - the catalogue: every new action has an icon, a default key that clashes with nothing, a tray entry and a "How do I…?"; the tray in every state; the Rooms & schedule section in Settings;
  - **on the real GPU**: the capture engine draws hidden parts (solid and blurred, frozen), the spotlight, an arrow, a box, text, the banner, a QR code, live captions and a logo into a hidden output window, takes a missing logo file in its stride, and switches everything off again without failing;
  - **speed** (budgets well above what the code needs, so a slow machine passes but a real slowdown doesn't): a busy GPU frame in six scenes (blurred parts, 200 lines, lines zigzagging across the TV, crossing highlighters with arrows and boxes, the overlays, everything) under one 60 Hz frame; the never-show check on 300 windows, hidden parts out and in, the helper's parser at pointer rate, replaying 500 marks, schedule checks, caption lines, the marks layer's updates, saving and loading full settings, the tray menu, a command-line round trip, a QR code.
- **Running the room (rev 7):**
  - scenes: tidied from the file (no source, a Wi-Fi code with its password, nonsense), described, on Ctrl+Alt+F1–F9 and `--scene`, saved and loaded;
  - playlists: the next item going round and skipping what can't be shown, tidied from the file, the start-up playlist must exist, made from favourites and scenes, on `--playlist`, in the tray and the schedule;
  - automatic hiding: email addresses, phone numbers in many forms, card numbers (Luhn) and IBANs (mod 97) checked, ID numbers; dates, amounts, versions, network addresses and plain numbers left alone; only the kinds chosen; boxes exactly over the words; capped; settings off unless chosen; **real text recognition** of a rendered picture finding the address where it is; **on the GPU**, the scan reading a picture and the box covering the address, then uncovering it;
  - transcripts: written as said, readable mid-way, subtitles at the end (minimum time, no overlaps), a folder that can't be written; captions knowing when each thing said began; sizes and places, top captions on the GPU, a top QR code below them; the caption look and automatic hiding reaching the helper;
  - the phone remote: signatures tied to key, session, counter and command; random keys and sessions; the key encrypted at rest; only listed commands; the rate limit; every verdict of the gate (accepted, replayed, older, wrong session, bad signature, not listed, too many, malformed); HTTP parsing and limits; private networks only; locked-down responses; the page's content security policy matching its script and style, nothing loaded from outside; **the page's JavaScript SHA-256 and HMAC run in Node** against .NET and RFC 4231; **the real server on loopback** (page, session, a signed command running once, a replay, a wrong key, a stale session, an unlisted command, garbage, a flood); **the real helper process** serving, passing on only what it accepted, and stopping when its input closes; the app side forwarding only listed commands;
  - next and previous slide reaching a real window as Page Down and Page Up;
  - every new setting saved and loaded (the key never readable in the file), nonsense made safe;
  - **memory**: a busy frame allocates under 2 KB (measured); **speed**: a page of 300 dense lines checked under 10 ms, a phone command's round trip under 50 ms. Timing tests run in their own collection so other tests can't skew them; the real capture engine's window test runs first (see GpuEngineCollection).
- **More from everything (rev 8):**
  - `--on` / `--off`: every switch read in any capitals and with `=`, in order with other actions, unknown names refused with the list, every toggle a real action; in the real app (stress harness `room`): on twice stays on, off turns it off, the outline off and back on in the saved settings, nonsense exit 2;
  - keys and passwords: every published key shape found; the value after a label covered and the label left readable; ordinary sentences about passwords and tokens left alone; only when chosen;
  - your own words: whole words in any capitals and spacing, special characters taken literally, the longer entry winning, only when chosen and listed, the list tidied and capped, carried as one protocol word and back (commas, spaces, percent signs, accents), a line that can't be checked in time covered whole; reaching the capture helper live and the recording helper at its start; **real text recognition** covering a labelled password and a listed name in a rendered picture, and not the title; **speed**: 300 busy lines with every kind and 40 words under 15 ms;
  - the phone remote: the new buttons allowed and lookalikes refused (`playlist-0`, `playlist-01`, `playlist-13`, other scripts' digits), every button on the page a listed command; **the page's script in Node against the real helper** pressing zoom, a playlist from the list, the break countdown and the name banner, and showing the PC's note;
  - contact cards: the MECARD text and its escaping, scanned back by the independent decoder, only the name under the code, the form's checks, too big refused, kept in a scene, settings made safe;
  - shuffle: never the same item twice running over 200 turns and reaching them all, skipping what can't be shown, a lone item staying, in order unchanged, saved and loaded;
  - web subtitles: the same cues with dots for milliseconds, commas in the words kept, both files written at the end and only when something was said;
  - scenes and live captions: described, saved and loaded as on, off, or (older scenes) left alone.
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

A real camera, switched on for a moment (its light comes on), delivering a frame through the helper's camera reader:

```
set SCP_CAMERA_TESTS=1
dotnet test --filter RealCameraTests
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
| `cli` | Command lines sent to the running copy: favourite, blank twice, unblank, an action, mistakes, then a burst of N | Exit codes 0, 1 and 2 where expected; no backlog; nothing left showing after `--stop` |
| `extras` | Zoom following the pointer, click rings, the pointer ring, the laser, a countdown, drawing a line, clicking a window to show it, the slideshow | The helper runs for the marks; the drawing layer opens and closes; privacy audit clean with each overlay open; no crash |
| `nevershow` | The test windows' app on the never-show list; tries a favourite, "the window I'm in" and the command line | Nothing is shown; the command line exits 1 |
| `stage` | Hides part of a window by dragging, the spotlight, the name banner, a QR code by command line, blank with the logo, the audience view, getting something ready and Go with nothing ready, live captions on and off, a room by command line, showing the hidden parts again | The helper runs for hidden parts; the part is remembered for the app; the audience view opens and closes; the speech helper starts and goes; an unknown room exits 1; privacy audit clean at every step |
| `room` | A scene by Ctrl+Alt+F1 and `--scene`, a playlist switching by itself and stopped, automatic hiding on and off, next / previous slide | The scene shows; the playlist switches and stops without stopping the picture; the helper runs for automatic hiding; privacy audit clean |
| `speed` | Times, end to end in the real app: showing a favourite, the spotlight bringing in the helper, the audience view opening, `--room` and `--qr` | Show ≤ 1 s, helper ≤ 2.5 s, audience view ≤ 600 ms, each command line ≤ 1.5 s (exe start included), on average |
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

For revision 7 (running the room), also:
- Scenes on a Stream Deck (through `--scene N`): each press switches the whole setup in one fade.
- A lobby PC: a playlist set to play at start, Start with Windows on; reboot: it plays by itself. Unplug the TV for a minute and plug it back: it carries on.
- Automatic hiding over an inbox, a CRM record and a spreadsheet of numbers while scrolling: addresses and numbers covered quickly; dates, totals and order numbers left alone.
- Live captions with "Save a transcript" on through a 30-minute talk; the subtitles line up when added to a recording of it.
- Phone remote with an iPhone and an Android phone: Windows' firewall question (choose Allow on private networks), pairing by scanning, next/previous slide in PowerPoint (while you type in another app), blank, freeze, scenes; "New code" stops the old phone; a laptop on a guest network or VPN can't reach it.

For revision 6 (stage tools), also:
- Hide part of an email window (solid, then blurred): on the TV, in a Zoom or Teams share of the output window and of the Zoom screen, through switches, freeze, a crop and zoom; scroll the email and the box stays put. Record the TV frame by frame during a switch: no frame shows what's under the box. A screenshot and a recording of that window have the box too.
- The audience view on a two-screen setup: it matches the TV (blank, countdown, drawings, banner, QR, small picture); it never appears on the TV or in a share.
- Get a window ready, rearrange it, then Go: the TV shows it as it now is.
- Arrows, boxes and text on a spreadsheet: readable from the back of the room on light and dark pages.
- The name banner and live captions at once: captions sit above the banner; a long name fits; captions follow normal speech and clear after a pause; during a break (blank with a message) nothing said appears.
- Scan the QR code from 3 m with an iPhone and an Android phone, for a link and for Wi-Fi (WPA and open): both read it first time and the Wi-Fi joins.
- A logo (PNG with transparency, a large JPEG): sharp above the message on a 4K TV; deleting the original keeps it.
- Save a room on one setup, change things, choose the room: everything comes back, and what's showing moves to the room's TV.
- A schedule two minutes ahead: it starts the slideshow on time; put the PC to sleep across an entry: it's skipped, not done late.
- A second TV (three screens): different things on each, blank one, close the window shown on the other; a never-show app on a screen the second TV shows turns it black.

For revision 5 (more for presenters), also:
- Click a window to show it on a two-screen setup: the outline and label never appear on the TV; a window half on the TV is outlined only on your side.
- With a TV: zoom in and move over a spreadsheet; the TV follows smoothly and the outline marks the zoomed part. Click rings and the pointer ring are easy to see from the back of the room, and never appear while frozen or when clicking a window in front of the shown one.
- Draw with pen and highlighter; the lines appear on the TV and in a Zoom or Teams share of the output window and of the Zoom screen; Esc clears both.
- A webcam and a document camera: listed, shown, sharp text on paper; unplugging one mid-show says so and the TV goes blank; another app (Teams) already using it.
- A small picture of the webcam over slides, in each corner and size; closing its window takes it off with a message.
- A countdown of 1 minute: it ends blank, the presenter brings it back.
- Never-show: show a whole screen, then open KeePass on it: the TV is black before the window is fully drawn; minimise it and the picture comes back.
- Plug a TV in with nothing showing: one offer, one click to show the last thing; unplug and replug within a few seconds: no second offer.
- Command line from a Stream Deck or a desktop shortcut (no console): a failure is shown on screen.
- Update check switched on: "Check now" with and without internet.

For revision 4 (modern look), also:
- On Windows 11 22H2+: Mica behind the title bars and the Settings section list, acrylic behind the quick-actions panel, in light and dark, over a busy wallpaper; text on them crisp and readable. On Windows 10: solid colours, icons from Segoe MDL2 Assets.
- High Contrast on and off while Settings and the panel are open: the backdrops come off and back.
- Animation effects off (Settings › Accessibility › Visual effects): no slide or fade anywhere.
- The quick-actions panel with the taskbar at the bottom, top, left and right; on a two-screen setup it opens on the screen you clicked, never on the TV; a second click on the icon closes it.
- "My Windows colour" with a few accents, including a very light and a very dark one.
