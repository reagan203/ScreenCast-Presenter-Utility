# SRS – ScreenCast Presenter Utility

Nov 23, 2026 · Revision 2 (same day): adds light/dark appearance, the presented-source outline, the extended shortcut set and command palette, screenshots and recording with audio, and load/stress acceptance tests. Changed requirements are marked **(rev 2)**.

ScreenCast Presenter is a lightweight Windows tray utility that mirrors one chosen window or screen region to a TV, a virtual display or a Zoom share, while the presenter keeps working privately on their main screen. It can also take screenshots and record what is presented. It targets under 60 MB of RAM while presenting, under 3% CPU and no bright flashes at any point.

## 1. Introduction

### 1.1 Purpose

This SRS defines the functional and non-functional requirements for ScreenCast Presenter (SCP) v1.0. It is the reference for design, development, testing and acceptance.

### 1.2 Problem statement

When sharing a full screen on Zoom or a TV, the audience sees everything: notifications, private windows, app switching. Zoom's single-window share breaks when the window is minimised, and switching what is shared causes white flashes and Zoom's bright borders. Existing tools such as OBS solve this but use 300 MB or more of RAM and are complex for non-technical users.

### 1.3 Scope

In scope for v1.0:

- Pick any open window, a monitor, or a cropped region of either as the source.
- Show it on a chosen output: a TV or second monitor (fullscreen), a virtual display for Zoom, or a floating window.
- Swap the source live with a smooth fade, from the tray or a hotkey.
- Light and dark interface that follows Windows, with no unpainted (white) frames; the output itself is always black when idle. **(rev 2)**
- A yellow outline on the presenter's screen around whatever is being presented. **(rev 2)**
- Screenshots and screen recordings (with optional meeting and microphone audio), saved on the device like the Snipping Tool. **(rev 2)**

Out of scope for v1.0: streaming over the network, annotation, video editing, macOS and Linux builds. **(rev 2: recording and audio capture moved into scope)**

### 1.4 Definitions

| Term | Meaning |
| --- | --- |
| Source | The window, monitor or region being presented |
| Output | Where the source is shown: TV, virtual display or floating window |
| Mirror window | SCP's borderless window that draws the source |
| DWM | Desktop Window Manager, the Windows compositor |
| DWM thumbnail | A Windows API that lets the compositor draw one window inside another with no pixel copying |
| WGC | Windows Graphics Capture API, a GPU-based frame capture API |
| Virtual display | A software monitor created by a driver, visible to Windows and Zoom like a real one |
| Flash | Any frame where more than 10% of the output is brighter than #303030 because content isn't ready yet |

## 2. Overall description

### 2.1 Product perspective

SCP is a standalone desktop app. It doesn't replace Zoom or Teams. It feeds them a clean screen to share. It relies on the Windows compositor (DWM) for zero-copy mirroring and on an optional virtual display driver for Zoom sharing without a physical second monitor.

### 2.2 User classes

| User | Needs | Skill level |
| --- | --- | --- |
| Presenter (primary) | Show one window to a room or call while working privately | Non-technical; expects one-click use |
| Trainer / teacher | Swap between slides, browser and code quickly | Moderate |
| Power user | Hotkeys, crop presets, command-line launch | Technical |

### 2.3 Operating environment

- Windows 10 version 2004 (build 19041) or later, and Windows 11. x64 and ARM64.
- Any GPU with a WDDM 2.0+ driver.
- Displays: laptop screen plus HDMI/USB-C TV, wireless display (Miracast), or a virtual display.
- Works alongside Zoom, Microsoft Teams, Google Meet (browser) and OBS.

### 2.4 Design and implementation constraints

- Language: C# on .NET 10 (LTS) with WinForms. No Electron, WPF or embedded browser.
- Single executable, portable (no install required); optional installer for the virtual display driver.
- No admin rights needed, except when installing the virtual display driver.
- No network access, telemetry or account sign-in.

### 2.5 Assumptions and dependencies

- DWM composition is always on (true on Windows 8 and later).
- Minimised windows can't be mirrored with DWM thumbnails; SCP falls back to WGC or warns the user.
- Zoom sharing without a second physical monitor depends on a third-party virtual display driver (for example Virtual Display Driver, MIT licence).
- Some protected content (DRM video, some banking apps) shows black by design; this is not a defect.

## 3. Functional requirements

Priority: **Must** = v1.0 release blocker, **Should** = v1.0 if time allows, **Could** = later.

### 3.1 Source selection

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-01 | List all visible top-level windows with title, app icon and a live thumbnail; hide tool windows, cloaked windows and SCP itself | Must |
| FR-02 | List all monitors as sources, labelled by name and resolution | Must |
| FR-03 | Filter the list by typing (app or title) | Should |
| FR-04 | Let the user drag a rectangle to crop the source to a region; remember the crop per app | Should |
| FR-05 | Pick a window by clicking it on screen ("pick mode") | Could |
| FR-06 | When the source window closes, fade the output to the idle screen and show a toast | Must |

### 3.2 Output

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-10 | Show the source fullscreen and borderless on a chosen monitor (TV), scaled to fit with black letterboxing | Must |
| FR-11 | Show the source in a floating, resizable, dark-framed window | Must |
| FR-12 | Detect a virtual display and offer it as "Zoom screen"; guide the user to share that screen in Zoom | Should |
| FR-13 | Keep the output window out of Alt+Tab and the taskbar when fullscreen on another monitor | Must |
| FR-14 | Prevent mirror loops: never list the output window as a source, and refuse a monitor source that hosts the active output. Do NOT apply `WDA_EXCLUDEFROMCAPTURE` to the output window, or Zoom and other capture tools will see black | Must |
| FR-15 | Show an optional small "Presenting" badge on the presenter's screen only, never on the output | Should |
| FR-16 | Idle screen when nothing is presented: solid #0B0B0C, optional logo or text, never white | Must |
| FR-17 | While presenting, draw a yellow outline on the presenter's screen around the presented window or screen, with a small "Showing on TV" tab. It is click-through, excluded from capture, follows the source when it moves or resizes, hides while the source is minimised, is never drawn on the output monitor, and can be turned off **(rev 2)** | Must |
| FR-18 | "Identify screens": show each monitor's number large on that monitor for about one second (first run and output selection) **(rev 2)** | Should |
| FR-19a | Now-showing bar **(rev 2, user request)**: a small always-on-top bar on the presenter's screen while presenting or recording, like Teams' sharing bar: a live mini preview, what is shown and where, recording time. Click it to bring the presented window to the front; ⋯ / right-click for Blank, Freeze, Stop, recording controls and Hide; drag to move (remembered). Never on the TV, excluded from capture, never takes focus. Ctrl+Alt+N shows/hides it | Must |

### 3.3 Presentation control

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-20 | Start, pause (freeze last frame), blank (fade to black) and stop from the tray menu | Must |
| FR-21 | Global hotkeys, each user-configurable or switchable off, with a master "pause all shortcuts". Defaults (all Ctrl+Alt+…): P picker, Enter command palette, W show the active window, Backspace previous source, PgDn/PgUp next/previous window, S stop, B blank, Shift+B blank with message, F freeze, D dim (cycles 0–40%), M move output to next screen, O switch output type, 1–9 show favourite, Shift+1–9 save favourite, C crop, Shift+C remove crop, R start/stop recording, Shift+R pause recording, Shift+S screenshot, Y outline on/off, I badge on/off, K list shortcuts, Shift+P pause all shortcuts. **(rev 2)** | Must |
| FR-21a | A shortcut that another app already owns is reported (tray, badge, Settings) and the rest still work. A default shortcut whose keys type a character on the current keyboard layout (AltGr layouts, e.g. Ctrl+Alt+4 = € on UK) moves automatically to Ctrl+Alt+Shift+… and the user is told **(rev 2)** | Must |
| FR-22 | Switching source cross-fades through black over 150–250 ms | Must |
| FR-23 | Save up to 9 favourite sources (app + crop + output) for one-key recall | Should |
| FR-24 | Optionally hide the mouse cursor on the output | Should |
| FR-25 | Command palette: type any action or window name and press Enter; every action is reachable this way **(rev 2)** | Must |
| FR-26 | Shortcut cheat sheet (Ctrl+Alt+K) and a Settings page to view, search, change, disable and reset shortcuts **(rev 2)** | Must |
| FR-27 | Blank with message: the output fades to the idle screen showing a short text ("We'll be right back", editable) **(rev 2)** | Should |
| FR-28 | Rapid commands are coalesced: only the latest pending source switch runs, so key-mashing never builds a backlog of fades **(rev 2)** | Must |
| FR-29 | Only one SCP instance runs; launching it again opens the picker in the running instance **(rev 2)** | Must |

### 3.4 Capture engines

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-30 | Default engine: DWM thumbnail (`DwmRegisterThumbnail`), zero-copy | Must |
| FR-31 | Fallback engine: WGC (`Windows.Graphics.Capture`) for minimised windows, monitors and crops the DWM engine can't do | Must |
| FR-32 | Choose the engine automatically; show the active engine in Settings | Should |
| FR-33 | Disable the yellow WGC capture border where the OS allows (`IsBorderRequired = false`, Windows 11) | Should |

### 3.5 Settings and persistence

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-40 | Store settings in a small JSON file in %APPDATA%\ScreenCastPresenter | Must |
| FR-41 | Remember last source, output monitor, crop and hotkeys across restarts | Must |
| FR-42 | Optional start with Windows | Should |
| FR-43 | Command-line: `scp.exe --present "PowerPoint" --output 2` | Could |
| FR-44 | Appearance: Light (default), Dark, or Match Windows (follows live changes of the Windows setting); always respects High Contrast **(rev 2; Light made the default at the user's request)** | Must |
| FR-45 | Settings are written atomically; a corrupt file is backed up and replaced by defaults; unknown fields from newer versions are ignored **(rev 2)** | Must |

### 3.6 Screenshots and recording (rev 2)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-50 | Take a screenshot of: what is being presented (default), a window, a whole screen, or a dragged region | Must |
| FR-51 | Screenshots are saved automatically as PNG in Pictures\Screenshots (configurable) and copied to the clipboard, like the Snipping Tool; a small preview on the presenter's screen offers Open, Copy, Show in folder | Must |
| FR-52 | Record video of: what is being presented (default), a window, a whole screen, or a region; MP4 (H.264), saved automatically in Videos\Screen Recordings (configurable) | Must |
| FR-53 | Recording audio options, each on or off: meeting/computer audio (system playback loopback) and microphone (people in the room) | Must |
| FR-54 | Pause and resume recording; a visible recording indicator with elapsed time in the badge and tray, never on the output | Must |
| FR-55 | Recording survives source switches, blank and freeze (it records what the output shows), and stops cleanly with a playable file if the source closes, the disk fills or the app is quit | Must |
| FR-56 | SCP's own UI, the outline and the badge never appear in screenshots or recordings | Must |
| FR-57 | Background recording (e.g. a webinar the user's manager asked them to record): record a chosen window independently of presenting, while the user keeps working. Recording continues when the window is covered, unfocused or on another screen; SCP warns that a minimised window can't be recorded and suggests moving it aside (or onto a virtual "hidden" screen when one exists) | Must |
| FR-58 | App-only audio: record only the recorded app's sound (per-process loopback, Windows 10 build 20348+ / Windows 11), so the user's own notifications and media aren't recorded; fall back to all computer sound on older Windows; microphone optional | Must |
| FR-59 | Recording stops by itself when the recorded window closes (webinar ended), and optionally at a set time or after a set duration | Should |
| FR-60 | Long recordings (several hours): written in a crash-safe way so a crash or power cut loses at most a few seconds; free disk space is checked before and during recording with a warning well before it runs out; the PC is kept awake while recording | Must |
| FR-61 | Recording and presenting are independent: the user can present one thing while recording another, and switching, blanking or stopping the presentation doesn't affect a background recording | Must |

## 4. Non-functional requirements

### 4.1 Performance and memory

| ID | Requirement | Target |
| --- | --- | --- |
| NFR-01 | RAM (private working set), idle in tray | ≤ 25 MB |
| NFR-02 | RAM while presenting with the DWM engine | ≤ 40 MB |
| NFR-03 | RAM while presenting with the WGC engine at 1080p | ≤ 60 MB |
| NFR-04 | CPU while presenting (DWM engine), 4-core laptop | ≤ 1% |
| NFR-05 | CPU while presenting (WGC engine), 1080p60 | ≤ 3% |
| NFR-06 | Added latency, source to output | ≤ 1 frame (DWM), ≤ 2 frames (WGC) |
| NFR-07 | Cold start to tray icon | ≤ 1 s |
| NFR-08 | Picker opens after hotkey | ≤ 200 ms |
| NFR-09 | No memory growth over an 8-hour session | < 5 MB drift |
| NFR-09a | RAM while recording **(rev 2)** | No fixed cap; measured and reported; no growth over a 2-hour recording beyond 10 MB |
| NFR-09b | Handle, GDI and USER object counts over an 8-hour session **(rev 2)** | No growth beyond 2% |
| NFR-09c | Picker opens and source switches stay within NFR-08 / FR-22 with every CPU core at 100% load **(rev 2)** | Same limits |
| NFR-09d | Screenshot frame copy **(rev 2)** | One frame in CPU memory, released right after saving (the only exception to "never copy frames to CPU") |

How memory stays low:

- The DWM engine never copies pixels into the app; the compositor draws the source directly.
- The WGC engine uses a frame pool of 2 GPU textures and never copies frames to CPU memory.
- Picker thumbnails are DWM thumbnails too, created only while the picker is open and released when it closes.
- No browser engine, no image cache, lazy-loaded settings window, workstation (non-server, non-concurrent) GC.

### 4.2 Visual comfort (no bright flashes)

| ID | Requirement |
| --- | --- |
| NFR-10 | Every SCP window is created cloaked and only shown after its first full paint in its theme colours, so no default (white) frame ever appears. Output windows always use #0B0B0C regardless of theme **(rev 2)** |
| NFR-11 | Title bars use dark mode (`DWMWA_USE_IMMERSIVE_DARK_MODE`) |
| NFR-12 | Output windows are created cloaked (`DWMWA_CLOAK`). The DWM engine registers the thumbnail at opacity 0, calls `DwmFlush`, uncloaks, then raises thumbnail opacity to 255 over 150 ms (DWM gives no first-frame signal). The WGC engine uncloaks after its first presented frame |
| NFR-13 | Source switches, blank and stop always pass through black; no frame of the old or new source at full brightness during the fade |
| NFR-14 | No white or bright borders, highlights or Zoom-style coloured outlines on the output |
| NFR-15 | Optional "night mode" dimming of the output, 0–40% |
| NFR-16 | Measured: zero flash frames (see definition 1.4) in 100 consecutive source switches, captured at 60 fps |

### 4.3 Usability

- A first-time user presents a window to a TV within 3 clicks and 15 seconds, without reading help.
- Every action is reachable from the tray icon; hotkeys are optional.
- Plain-language labels ("Show on TV", "Show in Zoom"), no jargon like "DWM" outside Settings.
- Keyboard-navigable picker (arrows, Enter, Esc); works with Windows Narrator.
- Scales correctly at 100–250% DPI and on mixed-DPI setups (per-monitor DPI v2).
- First-run screen of at most 2 steps: pick output monitor, learn the picker hotkey.

### 4.4 Reliability

- If the TV is unplugged, the output closes silently and SCP returns to the tray; replugging restores it.
- A crash in a capture engine must not crash the app; it falls back to the other engine or the idle screen.
- Survives sleep and resume, display resolution changes and GPU driver resets.

### 4.5 Privacy and security

- Nothing except the chosen source ever appears on the output.
- Toast notifications and SCP's own UI never appear on the output.
- **(rev 2) The audience never sees SCP or what it is doing.** Every SCP top-level window except the output (picker, palette, badge, outline, menus, tooltips, drop-downs, settings, first run) is excluded from capture at creation; a capture guard applies this to any window the UI framework creates on its own, and an automated audit checks it. SCP UI never opens on the output monitor. SCP never uses Windows notifications (they can appear on any screen); messages go to the badge. Shortcuts never move focus or the mouse, so the presented app does not change state when the presenter uses SCP.
- **(rev 2)** If the TV duplicates (mirrors) the presenter's screen, SCP warns the user to switch to Extend (Win+P), because in that mode the audience sees everything.
- No network calls, no telemetry, no crash upload unless the user opts in.
- Signed executable to avoid SmartScreen warnings.

## 5. System architecture

```
 Tray icon & hotkeys      Picker overlay         Settings store
 (Ctrl+Alt+P,B,F,1-9)     (thumbnails, search,   (JSON in %APPDATA%,
                           crop)                  favourites)
          \                     |                     /
           +--------> Session controller <-----------+
                (current source & output, fades, blank, freeze)
                     /                         \
     DWM thumbnail engine               WGC capture engine
     (default, zero-copy)               (fallback: minimised windows, monitors)
                     \                         /
                      +----> Output window <--+
              (black, cloaked until first frame, 150 ms fade-in)
                 /                |                   \
      TV / second monitor   Virtual display      Floating window
      (fullscreen)          (shared in Zoom)     (resizable, dark)
```

The controller uses the DWM engine by default and switches to WGC only for minimised windows or whole monitors. Only the output window ever reaches the TV or Zoom, so the presenter's other windows, notifications and SCP's own UI stay private.

**Capture helper process (rev 2, M3).** Everything that needs the GPU (the WGC engine, freeze, screenshots, and from M4 recording) runs in a short-lived helper: the same exe started with `--capture-host`/`--still`. It draws into a disabled child window inside SCP's output window, so cloaking, fades, topmost and privacy behave exactly as with the DWM engine. Reasons, measured on the development laptop: creating a Direct3D device costs ~26 MB of private memory that a process never gets back, so in-process WGC left SCP at 28–41 MB after use (NFR-01 is 25 MB); with the helper, SCP stays at ~12–17 MB and returns to idle size as soon as the helper exits. A GPU driver crash or reset also only ends the helper; SCP falls back to the DWM engine (SRS 4.4). Measured while presenting with WGC: SCP 16.6 MB + helper 54 MB. The driver's ~26 MB is outside SCP's control; NFR-03 is therefore measured as the sum of both processes with the driver's share reported separately.

**Recording helper (rev 2, M4).** Recording runs in its own helper process (`--record`), separate from the presenting helper, so presenting and recording stay independent (FR-61). Captured frames stay on the GPU: they are composed with Direct2D into a small ring of textures and handed to the hardware H.264 encoder through Media Foundation's DXGI device manager. Audio (WASAPI: the recorded app's process tree, all computer sound, and/or the microphone, each converted by Windows to 48 kHz stereo) is mixed and written by recording time, with a small cushion per source so device-clock drift over many hours never accumulates. The file is fragmented MP4, so a crash or power cut loses at most the last fragment (FR-60). The helper keeps the PC awake while recording, checks free disk space every second, stops by itself when the recorded window closes, and finishes the file on its own if SCP exits or crashes. "What the TV shows" is recorded from the output's screen cropped to the output: capturing the output window itself would miss the DWM thumbnail it displays, because window capture leaves thumbnails out (measured: black frames). SCP's own windows are excluded from capture, so they never appear (FR-56). Measured on the development laptop: about 2.4 s from the shortcut to recording (about 2 s of it is the graphics driver loading its encoder); helper ~120 MB with a still window and ~170–230 MB with full-motion video plus audio, flat over long recordings (NFR-09a); SCP itself is unaffected.

**Outputs for Zoom (rev 2, M5).** A screen from a virtual display driver (reported by Windows as an indirect virtual display, or named "virtual"/"IDD…") is labelled "Zoom screen"; the output choice prefers a real TV and uses the Zoom screen only when chosen or when it's the only extra screen. Without a virtual display, the output can be a floating window (FR-11): an ordinary, resizable window with a dark title bar, in the taskbar and Alt+Tab so Zoom lists it as "ScreenCast Presenter output", kept on top of other windows while presenting ("Keep the window on top", on by default, at the user's request), and not excluded from capture. Showing the very screen the window sits on (one-screen laptops) makes it a private preview, like Teams' sharing preview: hidden from capture, so sharing that screen in Teams or Zoom never shows it and it never mirrors itself; it becomes shareable again as soon as it shows anything else. It reopens where it was last left (if its title bar is still on a connected screen). Closing it stops showing; minimising it warns that Zoom then shares nothing. Ctrl+Alt+O cycles through the screens and the window; "Show in Zoom" picks the Zoom screen if there is one, otherwise the window, and SCP says once what to choose in Zoom's Share Screen dialog. While the output is a window, the presenter's screen still counts as theirs (badge, picker, outline and crop all use it), and a whole-screen source on the window's own screen becomes the private preview described above (only a full-screen output refuses its own screen, FR-14). Recording "what the output shows" in a window records the shown window itself, because recording the window's area of the screen would also record anything covering it.

**Hardening (M6).** Settings window (General, Output, Recording & screenshots, Appearance, Advanced; every change applies immediately). Start with Windows through the user's Run key (FR-42), quiet when started at sign-in, repaired if the portable exe moves. The last thing shown is remembered (FR-41), so "previous source" works after a restart. After resume from sleep, unlock and remote-desktop reconnects, SCP re-checks screens, shortcuts and the output a moment later. Release: one self-contained single-file exe per architecture (x64, ARM64), uncompressed for memory (see docs/RELEASE.md).

**Screen capture compatibility.** A full-screen flip-model surface may be shown by the GPU directly ("independent flip"). Modern capture (Windows Graphics Capture, Desktop Duplication — used by Zoom, Teams, OBS and SCP's own screenshots) sees it; legacy GDI screen grabs may not.

## 6. External interfaces

### 6.1 User interface

Two themes **(rev 2)**: Light (the default), Dark, or Match Windows. Dark: background #0B0B0C, surfaces #17171A, text #E6E6E6, accent #4C8DFF. Light: background #F3F3F5, surfaces #FAFAFB, text #1B1B1F, accent #2563D9. Rounded corners, Segoe UI Variable, no pure white anywhere. The output and idle screen are always #0B0B0C. Approved mockup: the "ScreenCast Presenter UI" canvas (picker, badge, tray menu, first run, outline, shortcuts, command palette).

| Screen | Contents |
| --- | --- |
| Tray icon | Grey when idle, accent dot when presenting; left-click opens the picker, right-click opens the menu |
| Tray menu | Present…, Blank, Freeze, Stop, Favourites ▸, Output ▸ (monitor list), Settings, Quit |
| Picker | Centred overlay on the presenter's screen: search box, grid of live window thumbnails, monitors row, output selector (TV / Zoom screen / Floating), "Crop…" button |
| Crop overlay | Dimmed source with a draggable rectangle and a size readout; Enter confirms, Esc cancels |
| Presenting badge | Small pill at the top of the presenter's screen: source name, Blank, Stop; auto-hides after 3 s |
| Settings | Tabs: General (start with Windows, cursor), Keyboard shortcuts, Output (monitor, dimming, idle screen text), Recording (folders, audio), Appearance, Advanced (capture engine, diagnostics) **(rev 2)** |
| Output | Borderless, black, source scaled to fit; nothing else |
| Outline **(rev 2)** | Yellow (#FFC83D dark / #F2B200 light) edge around the presented source with a "Showing on TV" tab |
| Command palette **(rev 2)** | Centred search box listing matching actions (with their shortcuts) and windows |

### 6.2 Software interfaces (Windows APIs)

| API | Use |
| --- | --- |
| `EnumWindows`, `GetWindowText`, `DwmGetWindowAttribute(DWMWA_CLOAKED)` | Build the window list |
| `DwmRegisterThumbnail`, `DwmUpdateThumbnailProperties`, `DwmFlush` | DWM engine: mirror, crop (`rcSource`), opacity fades |
| `Windows.Graphics.Capture`, `Direct3D11CaptureFramePool` | WGC engine |
| Direct3D 11 + DXGI swap chain (flip model) | Draw WGC frames and fades |
| `EnumDisplayMonitors`, `GetMonitorInfo`, `WM_DISPLAYCHANGE` | Monitor list and hot-plug |
| `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | Keep SCP's own UI (picker, crop overlay, badge, settings) out of captures; never applied to the output window |
| `DwmSetWindowAttribute` (dark mode, cloak) | No-flash window creation |
| `RegisterHotKey` | Global hotkeys |
| `Shell_NotifyIcon` | Tray icon |

### 6.3 Hardware interfaces

- HDMI, DisplayPort or USB-C display connected to a TV or projector.
- Miracast wireless display (Win+K), treated as a normal monitor.
- Virtual display from an IddCx (Indirect Display) driver, for Zoom without a second screen.

### 6.4 Interaction with Zoom and Teams

SCP does not integrate with Zoom's API. The user shares the virtual display (or the TV monitor) as a screen in Zoom. Because only SCP's output lives on that screen, the audience never sees the presenter's other windows, and switching source in SCP never re-triggers Zoom's share dialog or its green border on the presenter's work.

## 7. Use cases

### UC-01 Present a window on the TV

**Precondition:** TV connected as an extended display.

1. User presses Ctrl+Alt+P; the picker opens on the laptop screen.
2. User clicks the PowerPoint thumbnail; output is "TV" (remembered).
3. SCP creates a cloaked black fullscreen window on the TV, registers the thumbnail, then fades it in.
4. User keeps working in other apps on the laptop; the TV shows only PowerPoint.

**Covers:** FR-01, FR-10, FR-22, NFR-12.

### UC-02 Present in a Zoom call without a second monitor

**Precondition:** Virtual display driver installed.

1. User picks a browser window and output "Zoom screen".
2. SCP shows the mirror fullscreen on the virtual display.
3. In Zoom, the user clicks Share Screen and picks the virtual display (SCP's badge tells them which one).
4. User switches to other work; participants see only the browser.

**Alternative:** no driver installed → SCP offers floating mode and explains how to share that window in Zoom.

### UC-03 Swap what is presented

1. User presses Ctrl+Alt+2 (favourite: VS Code, cropped to the editor).
2. Output fades to black (≤ 125 ms), source changes, fades in (≤ 125 ms).
3. Zoom keeps sharing the same screen; no share dialog, no flash.

### UC-04 Crop to part of a window

1. In the picker, user hovers a thumbnail and clicks Crop….
2. User drags a rectangle over the area to show and presses Enter.
3. Output shows only that area, scaled to fit; the crop is saved for that app.

### UC-05 Hide the screen for a moment

1. User presses Ctrl+Alt+B; output fades to the idle screen.
2. User presses Ctrl+Alt+B again; the source fades back.

### UC-06 Source window is minimised

1. User minimises the presented window.
2. SCP switches to the WGC engine if possible, or freezes the last frame and shows "Source minimised" on the presenter badge only.

## 8. Milestones and acceptance criteria

Estimated effort for one developer: about 6 weeks to v1.0.

1. **M1 – Core mirror (week 1).** Window list, DWM thumbnail into a black borderless window on a chosen monitor, no-flash creation. Exit: UC-01 works; NFR-02 and NFR-10–12 pass.
2. **M2 – Tray, picker and shortcuts (weeks 2–3).** Tray icon and menu, picker overlay with live thumbnails and search, full shortcut set with conflict/layout handling, command palette, shortcut settings, fades, blank (with message), dimming, favourites, badge, outline, light/dark, first run, single instance, stress harness and FlashCheck. Freeze moves to M3 **(rev 2)**. Exit: UC-03 and UC-05 work; NFR-08 and NFR-16 pass.
3. **M3 – Crop, WGC engine, freeze and screenshots (week 4).** Crop overlay, WGC fallback for minimised windows and monitors, engine auto-select, freeze (GPU-held frame), screenshots (FR-50, FR-51). Exit: UC-04 and UC-06 work; NFR-03 and NFR-05 pass.
4. **M4 – Recording (rev 2: moved up from M6).** Recording of presented content, a window (incl. background webinar recording while the user works), a screen or a region; meeting (app-only where supported) and microphone audio; pause/resume; indicator; crash-safe long recordings; auto-stop when the window closes. Exit: FR-52–FR-61 and NFR-09a pass.
5. **M5 – Zoom screen.** Virtual display detection, guided setup, floating-window mode. Exit: UC-02 works on Zoom and Teams.
6. **M6 – Hardening and release.** Settings window, DPI, hot-plug, sleep/resume, start with Windows, 8-hour soak test, single-file publish, signed build. Exit: all Must requirements pass.

### 8.1 Acceptance tests

| Test | Method | Pass when |
| --- | --- | --- |
| Memory | Process Explorer private bytes, idle and presenting, 1080p | Within NFR-01 to NFR-03 |
| Memory leak | 8-hour run with a source switch every 60 s | < 5 MB growth |
| CPU | Task Manager average over 5 min | Within NFR-04 and NFR-05 |
| No flash | Record the output at 60 fps during 100 switches; script checks the average brightness of every frame | Zero frames above the flash threshold |
| Privacy | Open notifications, Start menu and other apps while presenting | None appear on the output |
| Zoom capture | Share the virtual display in Zoom while presenting | Participants see the source, not black |
| Usability | 5 first-time users, no instructions, asked to show a window on the TV | 4 of 5 succeed in ≤ 15 s |
| Hot-plug | Unplug and replug the TV 10 times while presenting | No crash; output restores |
| Load **(rev 2)** | Stress harness: 300 open windows; 1,000 rapid switches; 10,000 shortcut presses; 4K/8K sources; sources resizing 60×/s; source closing mid-fade ×1,000 | No crash; NFR-08 and FR-22 timings hold; memory returns to baseline |
| Pressure **(rev 2)** | All CPU cores at 100%, heavy GPU load, low memory, SCP limited to 5% CPU | Still responsive; NFR-09c holds |
| Environment **(rev 2)** | Resolution/DPI/theme/keyboard-layout changes, Explorer restart, lock/unlock, RDP, sleep/resume, GPU driver reset | No crash; UI and output recover |
| Hostile apps **(rev 2)** | Hung, elevated, constantly resizing, rapidly retitling, UWP, fullscreen-exclusive and DRM windows | No hang in SCP; expected behaviour per case |
| Settings fuzz **(rev 2)** | Random-byte, huge, read-only, disk-full settings files | Defaults load; corrupt file backed up; no crash |

## Appendix A. Open-source references

| Project | Why study it | Language | Licence |
| --- | --- | --- | --- |
| [OnTopReplica](https://github.com/LorenzCK/OnTopReplica) | Closest existing tool: mirrors any window with DWM thumbnails, region crop | C# | MS-RL (changed files must stay open source) |
| [Win32CaptureSample](https://github.com/robmikh/Win32CaptureSample) | Reference code for WGC window and monitor capture in Win32 | C++ | MIT |
| [Magpie](https://github.com/Blinue/Magpie) | Lightweight fullscreen mirroring of a window using WGC, multi-monitor | C++ | GPL-3.0 |
| [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) | Virtual monitor for the "Zoom screen" output; installable via winget | C++ | MIT |
| [OBS Studio](https://github.com/obsproject/obs-studio) | Window capture and fullscreen projector patterns (heavy; study, don't copy) | C/C++ | GPL-2.0 |

Licence note: MIT code can be reused freely. Reusing GPL code makes SCP GPL. Reusing MS-RL code requires the changed files to stay open source. Studying them and writing your own code avoids all of this.

## Appendix B. Tech stack

| Layer | Choice |
| --- | --- |
| Language | C# on .NET 10 LTS, target `net10.0-windows10.0.19041.0` |
| UI | WinForms with custom dark drawing |
| Interop | Microsoft.Windows.CsWin32 |
| WGC and D3D11 | Vortice.Direct3D11, Vortice.DXGI |
| Settings | System.Text.Json |
| Tests | xUnit |
| Publish | Self-contained single-file exe (no trimming; WinForms doesn't support it) |
| Virtual display | `winget install --id=VirtualDrivers.Virtual-Display-Driver -e` |
