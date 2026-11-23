# SRS – ScreenCast Presenter Utility

Nov 23, 2026

ScreenCast Presenter is a lightweight Windows tray utility that mirrors one chosen window or screen region to a TV, a virtual display or a Zoom share, while the presenter keeps working privately on their main screen. It targets under 60 MB of RAM, under 3% CPU and no bright flashes at any point.

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
- Dark-only interface with no white frames.

Out of scope for v1.0: recording, streaming over the network, annotation, audio capture, macOS and Linux builds.

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

### 3.3 Presentation control

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-20 | Start, pause (freeze last frame), blank (fade to black) and stop from the tray menu | Must |
| FR-21 | Global hotkeys, user-configurable: Ctrl+Alt+P (picker), Ctrl+Alt+B (blank), Ctrl+Alt+F (freeze), Ctrl+Alt+1–9 (favourite sources) | Must |
| FR-22 | Switching source cross-fades through black over 150–250 ms | Must |
| FR-23 | Save up to 9 favourite sources (app + crop + output) for one-key recall | Should |
| FR-24 | Optionally hide the mouse cursor on the output | Should |

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

How memory stays low:

- The DWM engine never copies pixels into the app; the compositor draws the source directly.
- The WGC engine uses a frame pool of 2 GPU textures and never copies frames to CPU memory.
- Picker thumbnails are DWM thumbnails too, created only while the picker is open and released when it closes.
- No browser engine, no image cache, lazy-loaded settings window, workstation (non-server, non-concurrent) GC.

### 4.2 Visual comfort (no bright flashes)

| ID | Requirement |
| --- | --- |
| NFR-10 | Every SCP window uses a black (#0B0B0C) background brush from creation; no default white class brush |
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

## 6. External interfaces

### 6.1 User interface

One dark theme: background #0B0B0C, surfaces #17171A, text #E6E6E6, one accent colour (#4C8DFF). Rounded corners, Segoe UI Variable, no pure white anywhere.

| Screen | Contents |
| --- | --- |
| Tray icon | Grey when idle, accent dot when presenting; left-click opens the picker, right-click opens the menu |
| Tray menu | Present…, Blank, Freeze, Stop, Favourites ▸, Output ▸ (monitor list), Settings, Quit |
| Picker | Centred overlay on the presenter's screen: search box, grid of live window thumbnails, monitors row, output selector (TV / Zoom screen / Floating), "Crop…" button |
| Crop overlay | Dimmed source with a draggable rectangle and a size readout; Enter confirms, Esc cancels |
| Presenting badge | Small pill at the top of the presenter's screen: source name, Blank, Stop; auto-hides after 3 s |
| Settings | Tabs: General (start with Windows, cursor), Hotkeys, Output (monitor, dimming, idle screen text), Advanced (capture engine, diagnostics) |
| Output | Borderless, black, source scaled to fit; nothing else |

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
2. **M2 – Tray and picker (weeks 2–3).** Tray icon and menu, picker overlay with live thumbnails and search, hotkeys, fades, blank and freeze. Exit: UC-03 and UC-05 work; NFR-08 and NFR-16 pass.
3. **M3 – Crop and WGC engine (week 4).** Crop overlay, WGC fallback for minimised windows and monitors, engine auto-select. Exit: UC-04 and UC-06 work; NFR-03 and NFR-05 pass.
4. **M4 – Zoom screen (week 5).** Virtual display detection, guided setup, floating-window mode. Exit: UC-02 works on Zoom and Teams.
5. **M5 – Hardening and release (week 6).** Settings, persistence, DPI, hot-plug, sleep/resume, 8-hour soak test, signed build. Exit: all Must requirements pass.

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
