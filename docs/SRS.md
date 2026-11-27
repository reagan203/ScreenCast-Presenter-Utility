# SRS – ScreenCast Presenter Utility

Nov 23, 2026 · Revision 2 (same day): adds light/dark appearance, the presented-source outline, the extended shortcut set and command palette, screenshots and recording with audio, and load/stress acceptance tests. Changed requirements are marked **(rev 2)**.

Nov 25, 2026 · Revision 3: easier for non-technical users. A short tray menu that only lists what applies, messages that wrap, stay long enough to read and carry buttons, no jargon (one word for screens: "Screen 2"), one status indicator at a time, a first run that shows where the icon is and ends by showing something, a share guide and one-click Zoom screen setup, Search and help, text size, and adding SCP to the Start menu from the downloaded exe. Changed requirements are marked **(rev 3)**.

Nov 26, 2026 · Revision 5: more for presenters. Click a window to show it, a command line, a never-show list, a Do not disturb reminder, zooming in on the TV, click highlights and a ring around the pointer, a countdown on the blank screen, a talk timer, cameras as sources, a slideshow of favourites, an offer when a TV is plugged in, drawing and a laser pointer on the TV, a small picture in a corner, and an opt-in update check. Changed requirements are marked **(rev 5)**.

Nov 26, 2026 · Revision 9: fourteen more. Split screen; zoom that follows typing; shortcuts on the TV; pictures and text cards as sources and in playlists; password boxes covered exactly; a check before sharing; faces blurred; chapters and a timeline; saving the last minute; the TV on, off and to this PC's input over the cable; schedules from a calendar file; rules from the organisation and settings to a file; voice commands; speaker notes, encrypted to the phone. Changed requirements are marked **(rev 9)**.

Nov 26, 2026 · Revision 10: twenty-three more. More privacy (the taskbar and desktop icons left out of a whole screen, black while an Open or Save box is open, a browser's page only, a warning when a meeting shares a whole screen); more to show (PDFs page by page, videos with sound, a whiteboard, the room sign); for trainers and stages (the small picture in a circle, Follow me, a timer on the TV, time budgets, PowerPoint's notes, a night filter, the speaker's screen, casting, rehearsals, words for captions, clicker keys); and from outside (`--status` and `--is`, copying the text, GIFs). Changed requirements are marked **(rev 10)**.

Nov 27, 2026 · Revision 11: a plan for macOS and Linux, not built yet. It covers what works on each system and what can't, a shared core with a front end for each system, the permissions each system asks for, how privacy holds on each, the order to build in, and the questions and decisions to settle first. v1.0 stays Windows-only. The plan is section 9; changed requirements are marked **(rev 11)**. It also fixes what a user saw on Windows with the output in a window on one screen: the preview seemed to minimise, the picture shook on switches and in browsers, and Settings flickered (FR-11, FR-113, FR-133, 6.1).

Nov 26, 2026 · Revision 8: more from everything. Exact on/off for a dozen switches from the command line; automatic hiding of keys, passwords and your own list of names, in screenshots and recordings too; zoom, the name banner, live captions, a break countdown and playlists from the phone; a contact card as a QR code; shuffled playlists; web subtitles; scenes that switch live captions. It also finishes FR-16 (the idle screen's message and logo when what's shown closes), FR-51 (the preview after a screenshot) and FR-59 (stopping a recording at a set time). Changed requirements are marked **(rev 8)**.

Nov 26, 2026 · Revision 7: running the room. Scenes on one key, playlists for lobby screens, hiding email addresses and numbers automatically, transcripts and readable captions, and a phone remote. Changed requirements are marked **(rev 7)**.

Nov 26, 2026 · Revision 6: stage tools. Hide part of a window, see exactly what the audience sees, get the next thing ready and then switch, a spotlight, arrows, boxes and text on the TV, a name banner, your logo on the blank screen, a QR code made on the PC, room profiles, schedules, live captions worked out on the PC, and a second TV. Changed requirements are marked **(rev 6)**.

Nov 25, 2026 · Revision 4: a Windows 11 look. Icons from Windows' icon font throughout, Mica and acrylic backdrops, the Windows 11 type ramp, short entrance animations, an option to use the Windows accent colour, Settings laid out like Windows Settings, and a quick-actions panel on left-click of the tray icon. Changed requirements are marked **(rev 4)**.

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
- **(rev 5)** Presenting aids on the TV (zoom, click rings, pointer ring, laser, drawing, a small picture in a corner), cameras as sources, privacy aids (a never-show list, a Do not disturb reminder), a command line, and an opt-in update check (section 3.7).
- **(rev 6)** Stage tools: hiding part of a window, the audience view, getting the next thing ready, a spotlight, arrows, boxes and text, a name banner, a logo, QR codes, room profiles, schedules, live captions and a second TV (section 3.8).
- **(rev 7)** Running the room: scenes, playlists for lobby screens, automatic hiding of personal details, transcripts, and a phone remote (section 3.9).
- **(rev 9)** Split screen, typing-aware zoom, shortcuts on the TV, pictures and cards, exact password-box covering, a check before sharing, face blurring, chapters and timelines, the last minute saved on demand, the TV over the cable, calendar schedules, organisation rules and settings transfer, voice commands, and speaker notes (section 3.11).
- **(rev 10)** Twenty-three more: the taskbar, desktop icons and Open/Save boxes kept off a whole screen, a browser's page only, a warning when a meeting shares a whole screen, PDFs, videos, a whiteboard, the room sign, the small picture's shape, Follow me, a timer on the TV, time budgets, PowerPoint's notes, a night filter, the speaker's screen, casting, rehearsals, words for captions, clicker keys, `--status` and `--is`, copying text and GIFs (section 3.12).
- **(rev 8)** More from each of these: exact switches from the command line, keys, passwords and your own words hidden automatically, a fuller phone remote, contact cards, shuffled playlists, web subtitles and scenes with captions (section 3.10).

- **(rev 11)** A plan, not yet built, for macOS and Linux versions (v2.0): section 9.

Out of scope for v1.0: streaming over the network, video editing, macOS and Linux builds. **(rev 2: recording and audio capture moved into scope; rev 5: annotation moved into scope as drawing on the TV, FR-71; rev 11: macOS and Linux planned for v2.0, section 9)**

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
- **(rev 11)** Planned for v2.0 (section 9): macOS 14 (Sonoma) or later on Apple silicon and Intel, and Linux desktops with PipeWire and xdg-desktop-portal (GNOME, KDE Plasma), on Wayland or X11.

### 2.4 Design and implementation constraints

- Language: C# on .NET 10 (LTS) with WinForms. No Electron, WPF or embedded browser.
- Single executable, portable (no install required); optional installer for the virtual display driver. **(rev 3)** The exe can add itself to the Start menu (per user, no admin rights).
- No admin rights needed, except when installing the virtual display driver.
- No network access, telemetry or account sign-in. **(rev 5)** One exception, off unless the user switches it on: the update check (FR-73), which runs in a separate helper process.
- **(rev 7)** The phone remote is the one feature that listens on the network: off unless switched on, local network only, in its own helper process, every command signed. Text recognition is Windows' own, on the PC.
- **(rev 6)** No third-party packages for the stage tools: QR codes are encoded in-house, speech recognition uses Windows' own SAPI through generated interop, and the microphone is opened only by the captions helper, only while captions are on.

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
| FR-05 | Pick a window by clicking it on screen ("pick mode"). **(rev 5, done)** Ctrl+Alt+Shift+W, or "Click a window…" in the picker: an invisible click layer covers the presenter's screens (never the TV), the window under the pointer gets a yellow outline and its name, and a click shows it. The click never reaches the app. Esc, a right-click or the shortcut again cancels. The desktop, the taskbar and windows in front of it can't be chosen; a never-show app's window says so (FR-62) | Should |
| FR-06 | When the source window closes, fade the output to the idle screen and show a toast | Must |

### 3.2 Output

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-10 | Show the source fullscreen and borderless on a chosen monitor (TV), scaled to fit with black letterboxing | Must |
| FR-10a | **(rev 4, from a user test)** Set to show in a window, a TV or screen connected since that choice (at start, on hot-plug, or just before presenting) gets the presentation: at once with "Keep it in a window" when nothing is showing; asked while showing in the window. Asked once per new screen; a virtual (Zoom) screen is never chosen this way | Must |
| FR-11 | Show the source in a floating, resizable, dark-framed window. **(rev 4)** It takes the shape of what it shows (no black bars) and keeps it while resized; only a close button (✕ stops showing); double-click brings the presented window to the front, or moves the pointer to the presented screen. As a private preview only (showing its own screen, hidden from capture), it steps behind other windows while the presenter works on that screen and comes back when clicked in the taskbar. A window a meeting could share never steps behind: some meeting apps share a window by copying its area of the screen and would send whatever covers it; the audience must only ever see the chosen source. **(rev 11, from a user report)** Stepping behind means just behind the window the presenter is working in, and no further (sent to the bottom of every window, it looked as if it had been minimised); switching between other windows leaves the order as the presenter made it. A new shape is taken at the moment of a switch or a new crop, while the picture is black, never a moment into the fade-in (the window jumped as the new picture came in); only a source being resized waits up to 150 ms | Must |
| FR-12 | Detect a virtual display and offer it as "Zoom screen"; guide the user to share that screen in Zoom. **(rev 3)** "Show me how" opens a three-step share guide (generic drawings of a meeting toolbar and share picker, the right tile outlined in yellow) for the window or the Zoom screen. "Set up a Zoom screen" installs the virtual display driver with winget after asking (Windows asks for admin rights), waits for the screen, makes it the output; without winget it offers the driver's page | Should |
| FR-13 | Keep the output window out of Alt+Tab and the taskbar when fullscreen on another monitor | Must |
| FR-14 | Prevent mirror loops: never list the output window as a source, and refuse a monitor source that hosts the active output. Do NOT apply `WDA_EXCLUDEFROMCAPTURE` to the output window, or Zoom and other capture tools will see black | Must |
| FR-15 | Show an optional small "Presenting" badge on the presenter's screen only, never on the output. **(rev 3)** Called the "status pill". While the now-showing bar (FR-19a) is visible the pill carries messages only, so one indicator shows the status at a time. Messages wrap to up to four lines, stay 5–15 s depending on length and buttons (longer while the mouse is over them), and can carry buttons that act on them ("Show in a window", "Show all of it", "Undo") instead of naming a shortcut | Should |
| FR-16 | Idle screen when nothing is presented: solid #0B0B0C, optional logo or text, never white | Must |
| FR-17 | While presenting, draw a yellow outline on the presenter's screen around the presented window or screen, with a small "Showing on TV" tab. It is click-through, excluded from capture, follows the source when it moves or resizes, hides while the source is minimised, is never drawn on the output monitor, and can be turned off **(rev 2)**. **(rev 4)** 2 px at 100% scaling. Around a window it is stacked directly above that window (not always on top), so windows in front of the presented one cover it too, and it follows the window when that comes to the front; around a whole screen it stays on top | Must |
| FR-18 | "Identify screens": show each monitor's number large on that monitor for about one second (first run and output selection) **(rev 2)** | Should |
| FR-19a | Now-showing bar **(rev 2, user request)**: a small always-on-top bar on the presenter's screen while presenting or recording, like Teams' sharing bar: a live mini preview, what is shown and where, recording time. Click it to bring the presented window to the front; ⋯ / right-click for Blank, Freeze, Stop, recording controls and Hide; drag to move (remembered). Never on the TV, excluded from capture, never takes focus. Ctrl+Alt+N shows/hides it | Must |

### 3.3 Presentation control

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-20 | Start, pause (freeze last frame), blank (fade to black) and stop from the tray menu. **(rev 3)** The menu lists only what applies now: show something (and "Show “…” again" after stopping), the presenting controls while presenting, screenshot, record, the recording controls while recording, Show on, Favourites once there are some, Search and help, Settings, Quit. Appearance, dimming, indicators, shortcuts and the capture method are in Settings | Must |
| FR-21 | Global hotkeys, each user-configurable or switchable off, with a master "pause all shortcuts". Defaults (all Ctrl+Alt+…): P picker, Enter command palette, W show the active window, Backspace previous source, PgDn/PgUp next/previous window, S stop, B blank, Shift+B blank with message, F freeze, D dim (cycles 0–40%), M move output to next screen, O switch output type, 1–9 show favourite, Shift+1–9 save favourite, C crop, Shift+C remove crop, R start/stop recording, Shift+R pause recording, Shift+S screenshot, Y outline on/off, I badge on/off, K list shortcuts, Shift+P pause all shortcuts. **(rev 2)** | Must |
| FR-21a | A shortcut that another app already owns is reported (tray, badge, Settings) and the rest still work. A default shortcut whose keys type a character on the current keyboard layout (AltGr layouts, e.g. Ctrl+Alt+4 = € on UK) moves automatically to Ctrl+Alt+Shift+… and the user is told **(rev 2)** | Must |
| FR-22 | Switching source cross-fades through black over 150–250 ms | Must |
| FR-23 | Save up to 9 favourite sources (app + crop + output) for one-key recall | Should |
| FR-24 | Optionally hide the mouse cursor on the output. **(rev 10)** It's left out by itself while the presentation covers the screen the shown window is on (no TV connected, so it's shown full screen on the presenter's own screen): the presenter's real pointer is already on top of the picture, and a copy inside it would be a second pointer, out of step with the first. It comes back when the TV is connected or the window moves to another screen | Should |
| FR-24a | **(rev 4, from a user test)** Browsers keep to the page chosen (on by default; Settings › Where it shows): a browser window shows whichever tab is active, so when its title shows another tab or page, the picture cuts to black at once (no fade) until it matches again; "Show this page instead" makes the new page the chosen one. Unread counters in titles ("(3) Inbox") are ignored | Must |
| FR-25 | Command palette: type any action or window name and press Enter; every action is reachable this way **(rev 2)**. **(rev 3)** Called "Search and help": "How do I…?" questions come first, and choosing one does the thing (or opens where it is done) | Must |
| FR-26 | Shortcut cheat sheet (Ctrl+Alt+K) and a Settings page to view, search, change, disable and reset shortcuts **(rev 2)** | Must |
| FR-27 | Blank with message: the output fades to the idle screen showing a short text ("We'll be right back", editable) **(rev 2)** | Should |
| FR-27a | After Stop, the message offers "Show it again" (the same window, crop and output) **(rev 3)** | Should |
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
| FR-43 | Command-line: `scp.exe --present "PowerPoint" --output 2` **(rev 5, done)** Also `--screen N`, `--camera "name"`, `--favourite N`, `--again`, `--output N/window/zoom`, `--blank`/`--unblank`, `--freeze`/`--unfreeze`, `--stop`, `--start-recording`/`--stop-recording`, `--screenshot`, `--choose`, `--action NAME` (any action id) and `--help`. The output is applied first, then what to show, then the actions in order; `--blank` and `--freeze` set a state rather than toggle. A launch goes to the running copy through a named pipe only the same user can open (one line of JSON each way, a few seconds' timeout for a silent client, one command at a time); if none is running, it is started first as its own quiet process (not inheriting the launch's console or pipes), so a script gets its answer at once. Actions that wait for the presenter (clicking a window, cropping) don't hold the pipe. Exit code 0 when done, 1 when it couldn't be done (the reason is printed and shown on screen), 2 for a mistake in the command (nothing is started). No file paths or arbitrary commands are accepted | Should |
| FR-44 | Appearance: Light (default), Dark, or Match Windows (follows live changes of the Windows setting); always respects High Contrast **(rev 2; Light made the default at the user's request)**. **(rev 4)** Accent colour: SCP blue (default) or the Windows accent (the shade Windows 11 uses for buttons in that mode), adjusted only as needed for 4.5:1 button text and 3:1 against the page; the presented-source outline stays yellow | Must |
| FR-45 | Settings are written atomically; a corrupt file is backed up and replaced by defaults; unknown fields from newer versions are ignored **(rev 2)** | Must |
| FR-46 | Text size: Normal, Large (115%) or Larger (130%) for SCP's own windows, menus and messages; the output is unaffected **(rev 3)** | Should |
| FR-47 | Add to the Start menu: a downloaded single-file exe copies itself to %LOCALAPPDATA%\Programs\ScreenCast Presenter, adds Start menu and desktop shortcuts and a Settings › Apps entry; offered in first run and Settings, and to replace a different installed build. Uninstall (Settings or Settings › Apps) closes the running copy, removes all of that and deletes the exe after exit; recordings, screenshots and settings are kept **(rev 3)** | Should |
| FR-48 | Keep the tray icon visible: on Windows 11, first run (on by default) and Settings set the icon to show next to the clock instead of behind ^ **(rev 3)** | Should |
| FR-49 | Quick-actions panel **(rev 4)**: left-click on the tray icon opens a panel above the clock, like Windows 11's Quick Settings: a live preview of what's showing, tiles for what applies now (Show something, Show it again, Blank, Freeze, Part of it, Screenshot, Record / Stop recording / Pause, Share in a meeting, My recordings), switched-on tiles in the accent colour and recording in red, and a footer with Stop showing, Search and help, Settings. Toggles keep it open; Esc, clicking elsewhere or a second click on the icon closes it. Never on the TV, excluded from capture, keyboard and Narrator accessible | Should |

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

### 3.7 More for presenters (rev 5)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-62 | Never-show list (Settings › Privacy), by program name, starting with common password managers. Their windows aren't offered in the picker or by "next window", and can't be shown by any route (shortcut, click, favourite, command line, small picture). While a whole screen is shown (main picture or small picture), a never-show window anywhere on the part shown cuts the picture to black at once, with no fade, until it's minimised, closed or moved away; its pop-ups count too. Being covered by other windows doesn't count as hidden. Watched through window events (shown, uncloaked, restored, moved, brought forward) with a once-a-second check as a backstop, only while a whole screen is shown. Something new is checked just before it can be seen, so it is never visible even for the length of a fade. Password managers inside a browser are part of the browser window and can't be told apart | Should |
| FR-63 | Do not disturb reminder: Windows doesn't let ordinary apps switch on Do not disturb (starting a focus session needs a token from Microsoft), so when all of the main screen is shown or recorded and Windows says pop-ups would show (`SHQueryUserNotificationState`), the presenter is reminded once per run with a button to the setting (Settings › Notifications on Windows 11, Focus assist on Windows 10) and "Don't remind me" | Should |
| FR-64 | Zoom in around the pointer (Ctrl+Alt+Z; Ctrl+Alt+Shift+Z closer: 1.5×, 2×, 3×, 4×, remembered). Zooming in and out passes through black; then the view follows the pointer smoothly while it's over what's shown (not while frozen or drawing). The view is a moving crop, so it never shows past the source or its crop. The outline marks the zoomed part. Ends when something else is shown or the crop changes. Works with both engines; a camera zooms on its centre | Should |
| FR-65 | Click highlights (Ctrl+Alt+H): a ring grows and fades on the TV where you click (yellow; blue for the secondary button, honouring swapped buttons). Ring around the pointer (Ctrl+Alt+Shift+H): a soft yellow ring follows the pointer on the TV. Only for clicks and the pointer over the shown window itself (not a window in front of it), never while the TV is frozen (the presenter may be working privately), and hidden with the picture when blank or held. Drawn inside the output by the capture helper, so every way of sharing sees them; switching them on shows the window again through the helper if needed | Should |
| FR-66 | Blank with a countdown (Ctrl+Alt+T, or a length from the menu; default 5 minutes): the TV shows the blank message and "Back in 4:59" on a second line, updated each second without a fade. At the end it says "Starting again in a moment" and stays blank: only the presenter brings it back (what's shown may have changed during the break). The key again shows it again. Showing again, stopping, or the window closing ends the countdown | Should |
| FR-67 | Talk timer in the now-showing bar, on the presenter's screen only: time since showing started (carries on through switches and blanks, resets on stop). With a talk length set, a message two minutes before the end and one at it. "Restart the talk timer" from Search and help | Should |
| FR-68 | Cameras (webcams, document cameras with a colour picture; infrared cameras are left out) as sources, listed in the picker next to the screens, as favourites (by Windows' id) and by `--camera`. Read in the capture helper through Windows' camera pipeline at up to Full HD (at least 15 fps, 30 preferred), so camera drivers never load into the app. Sharing with an app already using the camera where Windows allows. Plain reasons when it can't: no camera access (with where to turn it on), in use by another app, unplugged. Crop, drawing and recording a camera on its own aren't possible (it isn't on the presenter's screen); recording what the TV shows and screenshots of the TV are | Should |
| FR-69 | Slideshow (Ctrl+Alt+L, or Favourites › "Show them one after another"): the favourites take turns, 10 seconds to 5 minutes each, switching through black, skipping ones that can't be shown now (not open, not connected, never-show, or the TV's own screen). It waits while blank, frozen or held black, and stops when the presenter chooses something else or stops | Should |
| FR-70 | A TV or projector plugged in while nothing is shown: "Show “…” on it?" (the last thing shown) or "Choose what to show", in one click. Not for a virtual (Zoom) screen, not while set to show in a window (FR-10a covers that), not while a copying TV is being made separate. A screen counts as new when it wasn't there, or comes back after 30 seconds or more away (Windows reuses screen numbers); a flickering cable isn't an arrival. Can be switched off | Should |
| FR-71 | Drawing on the TV (Ctrl+Alt+Shift+D): a see-through layer covers exactly the part the audience sees, on the presenter's own screen, with a toolbar (pen, highlighter, six colours, undo, clear, laser, Done). Lines go to the TV as they're drawn, stored relative to the content so they stay on it when the window moves or the view changes. Esc or Done ends drawing and clears it from the TV; showing something else, stopping or minimising does too. Laser pointer (Ctrl+Alt+Shift+L): a red dot with a short fading trail follows the pointer over what's shown and the real pointer is hidden on the TV meanwhile; not while the TV is frozen, blank or held black (it would follow the presenter's private pointer). The layer and toolbar are hidden from capture | Should |
| FR-72 | A small picture in a corner (e.g. the presenter on a webcam over slides): another window, a screen or a camera, chosen in the picker; corner and size (20, 28 or 36% of the width) in Settings and the picker. Drawn inside the output by the helper, at the picture's brightness, frozen with it. Not the main picture itself, a never-show app or the TV's own screen. Taken off with a message when its window closes or its camera is unplugged, and when it becomes the main picture or showing stops | Should |
| FR-73 | Update check, off unless switched on (Settings › Advanced). At most once a day (30 seconds after start, never slowing it), a short-lived helper process (the exe with `--check-update`) asks GitHub for this project's latest release; the app itself opens no connection and loads no networking code. Only that question is sent. Drafts and pre-releases are ignored. A newer version is announced with "See what's new" (a release page link built from a checked tag, never a link from the network) and "Skip this version". Nothing is downloaded or installed. "Check now" says when it's up to date or why it couldn't tell | Could |

How the marks on the TV are drawn: they go to the capture helper as one-line commands (points as fractions of the content's whole area) and are drawn with Direct2D over the picture, inside the output window, so the TV, a meeting sharing the output window and one sharing the Zoom screen all see exactly the same. Turning any of them on shows what's shown again through the helper with a quick fade if it was shown the light way (the DWM engine can't draw). A new engine (after freezing or a fallback) gets the drawing, laser and small picture again.

### 3.8 Stage tools (rev 6)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-74 | Hide part of a window (Ctrl+Alt+X, or "Hide part of it…" in the tray): drag a box over what the audience mustn't see. The TV shows a solid dark box there (the default) or a heavy blur that keeps the page's shape but nothing readable (Settings › Privacy). Boxes are kept as fractions of the window's client area (or the screen), so they stay on the same spot of the window as it moves, resizes or scrolls, and are remembered per app (per screen for a whole screen), up to 12 each. Undo and "Show the hidden parts" (Ctrl+Alt+Shift+X) take them away; Settings › Privacy lists the apps with hidden parts. **Privacy:** the session covers them before anything fades in, on every engine that takes over (a switch, a restart, freezing, a fallback); something with hidden parts is never shown by an engine that can't cover them (the DWM engine): it goes through the helper, and if that can't start it isn't shown at all. A solid box is drawn fully opaque at every brightness, so nothing under it shows even mid-fade; while fading, a blurred part is drawn solid. They stay while frozen. Screenshots and recordings of that window or screen cover them with solid boxes too; the TV's own picture (and so "record what the TV shows") has them already | Should |
| FR-75 | See exactly what the audience sees (Ctrl+Alt+V): a small private window on the presenter's screen with a live copy of the TV's own window (a DWM thumbnail of the output), so blank, freeze, the countdown, drawings, hidden parts, the banner, the QR code and the small picture all show as the audience sees them, with a line saying what's on. Hidden from capture; resizable; remembers where it was. While it's open the picture goes through the helper (a copy of the light way's own copy shows nothing) | Should |
| FR-76 | Get the next thing ready, then switch (Ctrl+Alt+Shift+G, then Ctrl+Alt+G): the picker in a "what's next" mode; the choice appears privately in the same window (a live thumbnail of the window as it will show, with its remembered crop; a line for a screen or camera), and Go shows it through black like any switch. Never-show apps can't be made ready; a window that closed meanwhile is said so; Go with nothing ready says how to get something ready. The tray shows "Show “…” now" while something is ready | Should |
| FR-77 | Spotlight (Ctrl+Alt+Shift+F): everything on the TV is dimmed (72%) except a soft-edged circle around the pointer, following it over what's shown. Not while frozen, blank or held black (it follows the presenter's private pointer), and only over the shown window itself | Should |
| FR-78 | Arrows, boxes and text, in the drawing toolbar (A, B, T): an arrow from where the drag starts to where it ends, a rectangle between the drag's corners, and text placed where you click (typed in a small box, one line, up to 120 characters, shown with a soft shadow so it reads on light and dark pages). They follow the content like drawn lines and can be undone; a shape only clicked, not dragged, isn't kept | Should |
| FR-79 | Name banner (Ctrl+Alt+Shift+N): a lower-third banner on the TV with a name and a second line (e.g. "Jane Doe · Head of Sales"), as wide as its words, fading in and out. Asked for the first time it's used; changeable in Settings › Where it shows. It stays through switches until taken off, shows over the blank screen's message too, and never on plain black (blank without a message, or held black) | Should |
| FR-80 | Your logo on the blank screen (Settings › Where it shows): a PNG, JPEG, BMP or GIF (up to 10 MB) shown above the message when the TV is blanked with a message, centred, at most 40% of the width and 30% of the height, never blown up past twice its size. A copy is kept next to the settings, so moving or deleting the original doesn't lose it; a missing or unreadable file just means no logo. Drawn by both the helper and the output window itself | Could |
| FR-81 | A QR code on the TV (Ctrl+Alt+Q, or `--qr`): for a link or Wi-Fi details (WPA or an open network), made on the PC itself to ISO/IEC 18004 (byte mode, UTF-8, level M, versions 1–10, up to 213 bytes), with no internet and no third-party code. Shown on a white card with the quiet zone readers need, whole-pixel modules, in a chosen corner (moving out of the small picture's corner), above the banner and captions when at the bottom, labelled with the link or the network's name, never the password. The last link and network name are remembered; a Wi-Fi password never is | Should |
| FR-82 | Room profiles (tray › Rooms, Settings › Rooms & schedule, `--room`): "Save this setup as a room" keeps the TV it uses (or the window), the second TV, dimming, the pointer, click rings and the pointer ring, the blank message and logo, the name banner and, if chosen, the favourites. Choosing a room sets all of that at once, moving what's showing to the room's TV. Up to 12, by name | Could |
| FR-83 | Schedules (Settings › Rooms & schedule): do something at a set time on chosen days (weekdays, every day, weekends or one day): start or stop the slideshow, show a favourite, blank the TV (with its message), show it again, stop, or set up for a room. Checked every 30 seconds by this PC's clock, only while some entry is switched on. Something missed by more than 5 minutes (the PC asleep, the app not running) is skipped rather than done late; the clock going back never repeats anything; each action is harmless to repeat. Up to 20 entries; the next one is shown in Settings | Could |
| FR-84 | Live captions (Ctrl+Alt+Shift+T): what the presenter says shows along the bottom of the TV, above the banner. Windows' own offline speech recognizer (SAPI, the language set in Windows) runs in a separate helper process (the exe with `--captions`) and listens to the default microphone only while captions are on; nothing is recorded or sent anywhere. The last couple of sentences show, with the current guess, and clear after 6 seconds of silence. They show only with the picture: never on a break screen (blank with a message) or on black, so what's said in the room during a break never appears. No speech language installed, no microphone or no permission each get a plain reason | Could |
| FR-85 | A second TV (tray › Show on › "Second TV: show something…", or the `second-tv` action): with three or more screens, a second, independent session shows something else full screen on another screen (not the first TV, not the presenter's own screen). It has its own fades, blank, hidden parts and never-show protection (a never-show app on a screen it shows turns it black at once), its own labelled yellow outline, and stops when its screen goes away or the first TV moves onto it. A screen used by either TV can't be shown on a TV | Could |

The stage tools reach the helper as one-line commands like the marks before them (FR-65, FR-71); text, names and paths go last on their line so they may contain spaces, and line breaks are replaced. A new engine gets the banner, QR code, live captions and logo again; hidden parts are applied by the session itself. Finished highlighter lines are cut into triangles once and redrawn from those, and every line's shape is kept between frames, so a large drawing costs almost nothing a frame.

### 3.9 Running the room (rev 7)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-86 | Scenes 1–9 (Ctrl+Alt+F1 to F9, `--scene N`, a button deck through the command line): "Save this as a scene" keeps what's shown with its crop, the small picture and its place, the name banner, a QR code for a link (never a Wi-Fi code, which holds a password), the spotlight and dimming. Showing a scene shows its source through black, then sets everything around it. A source that isn't open says so. Hidden parts aren't part of a scene: they belong to the app and always apply. Listed in the tray and in Settings, where they can be shown or removed | Should |
| FR-87 | Playlists (lobby and waiting-room screens): favourites and scenes taking turns on the TV or the second TV, each for 10 seconds to 5 minutes, round and round, skipping what can't be shown now. Started from the tray, Settings, a schedule (start and stop), `--playlist NAME` / `--playlist off`, or when ScreenCast Presenter starts (with "Start with Windows", a PC that boots plays it by itself). Waits while that TV is blank, frozen or held black; stops when the presenter chooses something else there; started again when the TV comes back after being unplugged. Up to 12, of up to 30 items | Could |
| FR-88 | Hide automatically (Settings › Privacy, the `auto-hide` action): email addresses, phone numbers, payment card numbers (Luhn-checked), IBANs (mod-97-checked) and ID numbers (US SSN, UK National Insurance), each chosen separately, off by default. In the capture helper, at most every 0.4 s and only when the picture changed, one frame is scaled on the GPU (at most 2560 pixels a side), read back once into a buffer that's reused, and read by Windows' own text recognizer (`Windows.Media.Ocr`) on a worker thread, on the PC; what's found is covered like a hidden part (solid or blurred). With it on, something new isn't shown until its first scan is done (up to 2 s). Dates, amounts, version numbers and network addresses aren't covered. It's a safety net a moment behind what's shown; hidden parts (FR-74) and the never-show list (FR-62) are the guarantees. Something newly covered is said once (not more than every 20 s) | Should |
| FR-89 | Transcripts and readable captions: "Save a transcript" writes everything live captions hear to a text file as it's said ("[00:01:23] text"; a crash keeps it), with SubRip subtitles (.srt) beside it when they stop (each line shown at least 1.5 s, never overlapping). Captions can be off the TV (transcript only), and then listen without anything being shown. Caption size (normal, large, extra large) and place (bottom, or top so slide footers stay clear); a top QR code moves below top captions. The transcripts folder is chosen in Settings (Documents\ScreenCast Presenter\Transcripts by default) | Should |
| FR-90 | Phone remote (Settings › General, tray › Rooms, scenes and playlists): a phone on the same Wi-Fi as a clicker: next and previous slide (Page Down / Page Up to the shown window; typed when it's the active window, otherwise posted so the presenter keeps the focus), blank, freeze, spotlight, "show what's ready", previous, stop, favourites and scenes by name. Off unless switched on. Pairing: a QR code on the presenter's own screen, never on the TV, hidden from capture. All network code runs in a helper process (the exe with `--remote`); the app itself never listens. The pairing key is random (32 bytes), kept encrypted for the Windows user (DPAPI), and only ever in the address's #fragment, which browsers never send; every command is signed with it (HMAC-SHA256) over the helper's session (new each start) and a rising counter, so overheard commands can't be forged or replayed. Only listed commands are taken, only from private network addresses, at most 15 a second, one small request per connection (4 KB of headers, 1 KB body, a 5 s limit, 8 connections). The page is one file with a content security policy allowing only its own script and style (by hash). "New code" makes a new key, so phones paired before stop working. The port is a setting (47820 by default; any free port if taken) | Could |

How these stay light: nothing runs until it's used. Playlists run a timer only while playing; text recognition runs only while automatic hiding is on, in the capture helper; the remote runs only while switched on, in its own process; transcripts write a line at a time.

### 3.10 More from everything (rev 8)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-91 | Exact switches from the command line: `--on NAME` and `--off NAME` for the spotlight, the name banner (`caption`), live captions, automatic hiding, the laser, click highlights, the pointer ring, the phone remote, the audience view, the outline, the now-showing bar (`badge`) and the widget. Nothing happens when it already is so, so a button deck can't get out of step. Exit 1 with the reason when it couldn't be done (e.g. the spotlight with nothing shown); an unknown name is a mistake (exit 2) that lists the names | Should |
| FR-92 | Hide automatically, two more kinds (Settings › Privacy, each chosen separately): **keys and passwords**, by their published shapes (AWS access keys, GitHub and Slack tokens, Stripe and "sk-" keys, JSON web tokens) and the value after "password", "passcode", "PIN", "secret", "API key", "access key" or "token" followed by ":" or "=" (only the value is covered; at least 3 characters); and **words from your list**: up to 40 names or words of up to 60 characters (client names, code names), found whole, in any capitals, with any spacing between words; a longer entry wins over a shorter one inside it. A line that can't be checked against the list within 250 ms is covered whole. Typing words switches the kind on. Like the other kinds, on the TV, in screenshots and in recordings | Should |
| FR-93 | Phone remote, more buttons: zoom in and out, the name banner, live captions, the break countdown, and the saved playlists by name (sent by number). Still a fixed list, and still nothing that opens a window: the name banner without a name tells the phone to type it on the PC. What the PC says about a command (e.g. "Show something first") is shown on the phone | Could |
| FR-94 | A contact card as a QR code: a name with a phone number, an email address or both, as a MECARD that phone cameras offer to save. Only the name is written under the code. The form opens on the kind made last and offers the last card again; a scene may keep a card (it holds no secret) | Could |
| FR-95 | Shuffled playlists: a different order each time round, never the same item twice running (a lone item that can be shown stays). Chosen when a playlist is made, or with "Shuffle" / "In order" in Settings | Could |
| FR-96 | Web subtitles: a transcript's subtitles are also written as WebVTT (.vtt), for web players and slide tools, with the same timing as the .srt | Could |
| FR-97 | Scenes keep live captions on or off, and showing one switches them to match. A scene saved before scenes kept them leaves live captions as they are | Could |

How these stay light: the new kinds of hiding are two more source-generated expressions over the line text already built for the other kinds, run only while chosen; the presenter's words are one expression made once when the list changes (never per frame); shuffling picks from the items that can be shown, with no list kept between turns.

### 3.11 Fourteen more (rev 9)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-98 | Password boxes covered exactly (a kind of automatic hiding, in "All"): Windows' UI Automation says where every password box in the shown window is (for a whole screen, in up to six windows on it, front first); each is covered where it is, whatever it shows (dots, a revealed password, a browser's autofill), grown 4 pixels, at most 32. Asked again every 0.2 s (0.4 s for a screen) on its own thread, through raw COM calls so hours of asking leave no memory behind; something new waits for its first answer. On the TV, in screenshots and in recordings | Should |
| FR-99 | Zoom that follows typing: "Zoom follows" the mouse, where you type, or both (typing while it's recent, the mouse when it moves). The text cursor comes from Windows' caret (classic apps, Office, browsers that support magnifiers), or from UI Automation's focused field when that's a single line. Only where, never what's typed | Could |
| FR-100 | Faces blurred (off unless chosen; "Everyone" or "Everyone but me", which keeps the largest face, the presenter at their own camera, clear): Windows' own face detection on a copy of what's shown (at most 960 pixels a side) every 0.2 s while it changes; each face's box grown by 30% (45% above). Blurred on the TV, solid in recordings, pixelated in screenshots. Nobody is recognised and nothing is kept | Should |
| FR-101 | Shortcuts on the TV (off unless chosen): a key combination pressed in what's shown appears in a chip at the bottom for 1.8 s ("Ctrl + Shift + S"). Only combinations with Ctrl, Alt or Win, and F1–F24 and Esc alone; never typing, never a combination that types a character on one of the presenter's keyboard layouts (AltGr), never SCP's own keys, never while frozen, blank or held, and only while the presenter works in what's shown. The keyboard is polled every 25 ms while it's on: no keyboard hook | Could |
| FR-102 | A check before sharing ("check-before-showing", and the "Check before showing" setting for the first time each window or screen is shown): one picture of it, nothing covered but the parts already hidden, read in a helper process for every kind (all text kinds, the presenter's words, password boxes, faces). A private window shows what was found, outlined and labelled, with "Show it as it is", "Hide these automatically, then show it" (switches on the missing kinds) and "Don't show it". From the setting, only something automatic hiding wouldn't cover is asked about. The preview never stays on disk | Should |
| FR-103 | Pictures and cards as sources: a picture file (PNG, JPEG, BMP, GIF, TIFF; up to 40 MB, shown at up to 4K) shown like a window (fades, zoom, marks, split screen, automatic hiding), read into memory so the file isn't locked; a card: up to six lines of words drawn into a 1920×1080 picture on the PC (the first line the title), made once per words and tidied when unused. Shown from the tray, "--picture", "--card", favourites and scenes. Playlists take a picture, a folder of pictures (each in turn, read again each time round, up to 200) and cards. Hiding parts by dragging, cropping and drawing say why they don't apply | Should |
| FR-104 | Split screen: the main picture and up to three others (windows, screens, cameras, pictures) side by side, stacked, in a grid, or one large and the rest small, with a gap between. Each other picture keeps the main one's privacy: never-show apps refused, its hidden parts covered (sent for that source), automatic hiding applied to it, black until its first look, and a whole screen beside it held black while a never-show app is on it. The small picture in a corner gets the same covering. From the tray, the picker, "--split", "--layout" and scenes | Should |
| FR-105 | Chapters and a timeline: each time what's shown changes while recording (another window, split screen, blank, a break), a chapter starts; a chapter under 5 s gives way to the next. When the recording is saved, chapters go beside it as WebVTT (.chapters.vtt), a list for video sites (.chapters.txt, "1:15 Demo") and an FFmpeg metadata file. "Save a timeline" (off by default) writes what was shown when to a text file in the transcripts folder as it happens | Could |
| FR-106 | The last minute (off unless switched on): while something is shown full screen, a recorder keeps the TV's picture (with its sound when recording sound is on) in 15-second parts in a temporary folder, the oldest deleted, so 30, 60 or 120 seconds are always kept. "Save the last minute" joins the newest parts into one MP4 in the recordings folder without encoding them again (each frame lasting until the next, so still moments keep their length). The parts are deleted when it stops, and at the next start after a crash | Could |
| FR-107 | The TV over the cable (DDC/CI): turn it on (to this PC's input) or off (standby), switch its input, from the tray, the schedule, a calendar booking, "--tv on/off" and "--tv-input hdmi1". "Turn the TV on when showing starts". "Test the TV" asks it what it supports and says so plainly (many TVs don't take these commands; monitors usually do). Off the UI thread | Could |
| FR-108 | Schedules from a calendar file: an .ics file of the room's bookings (exported or a synced copy; read on the PC, never fetched), read again when it changes. Before each booking (on time, or 5, 10 or 15 minutes early) and after it, a chosen action runs (any schedule action, including the TV on and off), for bookings whose title contains given words or all. Times in UTC, Windows or IANA time zones (a built-in table: the app runs without ICU) or local time; daily, weekly (days, interval) and monthly repeats with a count or an end; dates left out; cancelled and all-day events skipped. Back-to-back bookings: the end first, so the next setup wins | Could |
| FR-109 | Rules from the organisation (Group Policy or a script, in HKLM or HKCU\SOFTWARE\Policies\ScreenCastPresenter; the machine's win): never-show apps that can't be removed, kinds always hidden automatically, "Check before showing" always on, and the phone remote, recording, screenshots, the last minute, the update check and voice commands switched off. Shown in Settings as "Set by your organisation". Settings to a file and back (Settings, "--export-settings", "--import-settings"), without the phone's pairing key, window positions or the TV's name; loading restarts the app | Should |
| FR-110 | Voice commands (off unless switched on): Windows' own speech recognition, in its own process, only while something is shown. "Presenter, …" then a phrase: next/previous slide, blank and show the screen, freeze and go live, spotlight on/off, zoom in/out, take a break, show what's ready, show favourite or scene N, take a screenshot, save the last minute, stop showing. Only those, nothing more said in the same breath, nothing recorded; nothing that opens a window, changes a setting or records | Could |
| FR-111 | Speaker notes: up to 2,000 characters for each favourite and scene. A private notes window (on top, on the presenter's screen) shows the notes for what's on the TV and follows each switch; "Edit" and Settings' scenes edit them. The paired phone shows them too, encrypted with its pairing key: an HMAC-SHA256 keystream over a fresh 16-byte nonce, then an HMAC tag over the nonce and ciphertext checked first, so others on the Wi-Fi see only noise and a changed message is refused | Could |

How these stay light: each runs only while chosen. The password-box finder, the caret tracker and voice listening each have one thread or process while on; faces and text share the GPU's scaled copy and run on a worker only when the picture changed; the shortcut poll and the replay recorder run only while their switch is on and something is shown; calendars are read again only when the file changes.

### 3.12 Twenty-three more (rev 10)

Every new action is in the tray menu (On the TV, Show on, or Rooms, scenes and playlists), in Search and help with a "How do I…?", on the command line and in Settings; none has a default key (any can be given one). Each runs only while it's used.

**More privacy.** Each of these only ever shows less.

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-112 | Leave out the taskbar (on by default, Settings › Privacy): a whole screen shown stops short of its taskbar and any other docked bar (the screen's work area), plus the full thickness of a taskbar that hides itself (it would pop up over the picture). Kept as a crop, so zoom, marks, hidden parts, screenshots and recordings stop at it. A part the presenter chooses is kept inside it; a part that is all taskbar is refused rather than showing more (and when the taskbar moves onto a part already chosen, all that's clear of it now shows, never a crop worked out before the move). "Show all of it" means all but the taskbar. Applies to the main picture, a screen beside it, a screen shown small and the second TV, and follows the taskbar when it moves, starts hiding itself or a screen changes. At most 40% of a side is left out. A screen's part travels to the helper with it; hidden parts are kept by the screen, never by its part, so rounding can't lose them | Should |
| FR-113 | Hide desktop icons (on by default): while a whole screen is shown anywhere (main, beside, small, second TV) the desktop's icons are hidden and come back when it stops. Icons the presenter hid in Explorer are left alone; a restarted Explorer's new icons are hidden on the next check (every 2 s while hidden); that they're hidden is saved, so the next start after a crash shows them again. Only the icon list is hidden, never the wallpaper. **(rev 11)** Not for a private preview (FR-11): nobody else sees it, so the presenter's own desktop is left as it is | Should |
| FR-114 | Open and Save boxes (on by default): a file or folder box (a dialog box holding a file view, a folder tree or the Open/Save view) on the part of a screen being shown cuts the picture to black at once, like a never-show app, until it closes or moves away. On the second TV too. A window shown on its own is unaffected (its boxes are separate windows the audience doesn't see) | Should |
| FR-133 | Browsers: only the page (on by default): a browser window shown on the TV or the second TV shows only its page (beside the main picture or small in a corner it shows whole: those can't be cropped), so other tabs' titles, the address bar, bookmarks and the profile are left out. The page's area comes from the browser (Chromium's page window, or UI Automation's document for others), is asked for before showing (up to 0.9 s; otherwise it's shown whole until the answer comes), then every 0.5 s on a thread of its own, and followed without a fade. Not believed when under 25% of the window or 98.5% or more of it (full screen). A crop the presenter chose, or one saved for the app, wins. **(rev 11, from a user report)** No page, or one under 25%, is most often a page between two loads: the page's crop is kept for up to 2 s (four answers) rather than the picture jumping to the whole window, tabs and address bar included, and back; a page filling the window is taken at once. Before showing, the browser is asked again every 0.15 s within the same 0.9 s | Should |
| FR-134 | A meeting sharing a whole screen (on by default, while something is shown): every 3 s the windows of meeting apps (Zoom, Teams, Webex, Slack, Discord, GoTo, RingCentral) are looked at; a see-through, click-through border kept on top and following a screen's edges (within 24 px) means that screen is shared. Unless it's the TV showing the chosen source, the presenter is told once per share ("Zoom is sharing all of Screen 1…") with "Show me how" and "Don't warn me". A guess from outside: a browser meeting draws no border and isn't seen | Could |

**More to show.**

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-115 | PDFs (tray, `--pdf "file" [--page N]`, favourites, scenes, playlists): pages are made into pictures (the longer side at least 2,560 px, bigger on a scaled display) by Windows' own PDF renderer in a helper process (`--pdf-pages`, 30 s limit), kept in a folder named from the file's path, size and last change (the newest 8 PDFs kept), and shown as pictures. The page asked for is made first, then the next two and the one before, in the background. Next and previous slide (keys, clicker keys, the phone, voice) turn the page through black, saying so at either end. A favourite keeps the PDF and its page. In a playlist each page is a step (pages made only when shown; up to 200). Password-protected, damaged, empty, missing and too big (300 MB) files say why | Should |
| FR-116 | Videos (MP4, M4V, MOV, WMV, AVI, MKV, WebM; tray, `--video`, favourites, scenes, playlists): played by Windows' media player in the capture helper, frames copied GPU to GPU, sound on the default speakers. It plays only while the TV shows it, so it starts as it fades in and pauses while blank, held black or frozen; next and previous slide jump 10 s. At the end its last picture stays and the presenter is offered "Play it again"; in a playlist it plays to its end (a 4-hour backstop), then the next item shows (a playlist of just one video plays it again). A file Windows can't play (or with no picture) says why. A video is the main picture only (never small or beside); its frames go through automatic hiding like any picture. Every call into the player is made on the render thread, never under a lock or in one of the player's events | Should |
| FR-117 | The whiteboard (tray, `--whiteboard`, favourites and scenes): a blank board on the TV (dark by default, light when chosen: Settings › Where it shows) drawn on from a private board window (hidden from capture) with the drawing tools; the board window is the drawing area. The drawing stays on the TV between drawing sessions until "Clear" or the board is put away (which brings back what was shown before, or stops), and is saved as a PNG next to screenshots with "Save as a picture". Showing anything else puts the board away | Should |
| FR-125 | The room sign (tray, `--room-sign`, favourites, scenes, playlists): "free until 13:00", "Booked until 11:00" (back-to-back bookings joined) and up to three later bookings today, from the room's calendar file, on a dark sign with a green or red panel, made on the PC and read again in place (no fade) when it says something new (checked every 20 s). Meeting names only when chosen ("Booked" otherwise). A calendar that can't be read says "Bookings unavailable", never "Free" | Could |

**For trainers and stages.**

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-118 | The small picture's shape: rectangle (default), rounded corners or a circle (the middle square of the picture), from Settings, the tray, scenes and `inset-shape`. Its hidden parts and automatic covers are drawn inside the same mask | Could |
| FR-119 | Follow me (tray, `follow-me`, `--on/--off follow-me`): while on, switching to a window of an app on its list shows it (after it's stayed in front 450 ms, so Alt+Tab passing through doesn't switch). Anything else (another app, a dialog box, the desktop, a never-show app, a minimised window) stays private; nothing happens while frozen. Turning it on adds the app shown; showing another app while it's on adds that app (with Undo); up to 20, edited in Settings. Off when showing stops | Could |
| FR-120 | A timer on the TV (tray presets 1–30 minutes, or minutes and words; `--timer N\|off`): "Exercise 4:59" in a corner nothing else uses (not the QR code's or the small picture's), above the banner and captions, white on a dark chip, updated each second from a monotonic clock. At zero, "Time's up" in amber for 30 s, then it goes. With the picture or the blank screen's message, never on plain black; kept through engine changes; off when showing stops | Could |
| FR-121 | Time budgets: each favourite and scene can have minutes (the notes form). While shown, the now-showing bar says "Demo 3:12 of 5:00" (or "1:05 over"); a minute before and at the end the presenter is told, once each. Time adds up when something comes back, stops while blank or held, and starts again with the talk timer. Every part shown is timed, for rehearsals | Could |
| FR-122 | PowerPoint's notes (on by default): while a PowerPoint window is shown and the notes are seen somewhere (the notes window, the paired phone, the speaker's screen), PowerPoint is asked every 0.7 s, on a thread of its own, which slide is on (the slide show's, or the one being edited) and its notes; they join the presenter's own as "Slide 3 of 12: …". Read only; a busy or closing PowerPoint is asked again shortly | Could |
| FR-123 | The night filter (Settings, tray, `--night`, scenes): "Soften white pages" (a tone curve to 72%, warmer) or "Turn light pages dark" (inverted, hues kept by turning them half way round). On what's shown and the pictures beside it; never on the name banner, captions, the timer, the QR code (it must scan) or the small picture. Fades in from black like the picture (folded into the colour matrix), so it never flashes | Could |
| FR-124 | The speaker's screen (Show on, `speaker-screen`): full screen on a chosen screen that isn't a TV showing something: the time, how long the talk's run or what's left (amber two minutes before, red when over), the part's budget, what the audience sees (a live copy), what's next, the timer, and the notes in large type. Always dark, hidden from capture, never takes focus, Esc puts it away; warns when it covers the presenter's own screen | Could |
| FR-126 | Connect a wireless TV (Show on, `--cast`): opens Windows' own Cast list; once connected, the usual offer to show on it follows, as a separate screen (never a copy of the presenter's) | Could |
| FR-127 | Rehearse (tray, `rehearse`): a private practice. Each part shown is timed; live captions listen (even with nothing shown, and never on the TV meanwhile). "Finish" writes a report to the transcripts folder (time taken against the talk's length; words, pace and filler words, overall and per part; parts over their budget; the longest pause) and says the gist. Nothing is recorded or sent | Could |
| FR-128 | Words for captions (Settings): up to 100 names and words. They're added to Windows' speech dictionary while captions listen and taken out again after (words already there are left alone; words a helper that was ended couldn't take out are remembered and taken out at the next start), and what's heard is spelled as listed ("ninth mind" → "Ninthmind"; only whole words, three letters or more) | Could |
| FR-132 | Page Down and Page Up go to what's shown (tray, Settings, `clicker-keys`; off by default): while a window, a PDF or a video is shown, Page Down and Page Up (and the arrow keys, if chosen) are taken with RegisterHotKey (no keyboard hook) from any keyboard or presenter clicker and sent to what's shown (posted, never typed), so slides move while the presenter works elsewhere. They don't reach the presenter's own window meanwhile; keys another app has taken are said once | Could |

**Running it from outside.**

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-129 | `--status` answers with what's on as JSON (showing, what and where, blank, frozen, held, recording, the playlist, the timer's words, the part's budget, and every switch); `--is NAME` answers with exit code 0 when on and 1 when off, shown on screen as nothing (an answer, not a problem). New `--on/--off` names: follow-me, timer, night-filter, clicker-keys, speaker-screen, rehearse | Should |
| FR-130 | Copy the text in what's shown (tray, `--copy-text`): a window or screen pictured as a screenshot takes it (hidden parts and automatic hiding covered, so those words are never copied); a PDF page, picture, whiteboard, room sign, camera or video from the output window itself (exactly what the audience sees, covered as it is) is read by Windows' text recognizer in a helper (`--read-text`, 20 s limit) and put on the clipboard (up to 100,000 characters). Not for a video (freeze and screenshot it) | Could |
| FR-131 | A GIF of a recording (tray, `--gif`): the first 15 s of an MP4, at most 10 frames a second and 720 px wide, made in a helper (`--make-gif`, 3-minute limit) by an encoder written here (a palette per frame by median cut, LZW, looping), beside the recording, never over another file. A still moment keeps its length | Could |

How these stay light: the privacy checks run only while a whole screen or a browser is shown, the share watch only while something is shown, Follow me and clicker keys only while on, PowerPoint is asked only while its notes are seen, the room sign is checked only while shown, and PDFs, text and GIFs are made in short-lived helpers. Videos are decoded on the GPU by Windows.

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
| NFR-09e | A busy frame of marks on the GPU **(rev 6)**: 12 blurred hidden parts, 200 lines (pen, highlighter crossing itself, arrows, boxes), the spotlight, banner, QR code and live captions | ≤ 16 ms (one 60 Hz frame); measured on the real GPU by the tests |
| NFR-09g | Memory a busy frame of marks allocates **(rev 7)** | ≤ 2 KB (it was about 160 KB before the frame lists were kept); measured by the tests |
| NFR-09h | Running the room **(rev 7)** | A page of 300 dense lines checked for personal details ≤ 10 ms; a phone command's round trip ≤ 50 ms on the network; automatic hiding's buffers reused (no growth) |
| NFR-09i | Hiding keys and your own words **(rev 8)** | 300 busy lines (addresses, labelled passwords, tokens, listed names) checked with every kind on and a full list of 40 words ≤ 15 ms |
| NFR-09j | Fourteen more **(rev 9)** | Asking Windows for a window's password boxes ≤ 25 ms, and 600 asks grow memory < 4 MB; joining a minute of parts takes seconds (nothing encoded again); a frame of marks still ≤ 2 KB; nothing new runs while its switch is off |
| NFR-09f | Work done while presenting **(rev 6)** | Never-show check on 300 windows ≤ 0.5 ms; a schedule check ≤ 0.2 ms; a caption line ≤ 0.02 ms; a hidden-parts line out and in ≤ 0.1 ms; replaying 500 marks to a new engine ≤ 20 ms; a QR code ≤ 10 ms; the tray menu ≤ 0.5 ms; a command-line round trip ≤ 150 ms; full settings save or load ≤ 60 ms |

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
- Plain-language labels ("Show on TV", "Show in Zoom"), no jargon like "DWM" outside Settings. **(rev 3)** Not in Settings either, except "Now using: …" under Advanced. One word for a screen everywhere: "Screen 2" (Windows' number, as Identify shows it). The app is "ScreenCast Presenter" in all text, never "SCP"; an automated test checks the source.
- **(rev 3)** No message asks the user to remember a shortcut to act on it: it carries a button. Shortcuts are mentioned as an extra ("or press Ctrl+Alt+P").
- Keyboard-navigable picker (arrows, Enter, Esc); works with Windows Narrator.
- Scales correctly at 100–250% DPI and on mixed-DPI setups (per-monitor DPI v2).
- First-run screen of at most 2 steps: pick output monitor, learn the picker hotkey. **(rev 3)** Step 2 is "Find ScreenCast Presenter by the clock": a drawing of the taskbar corner with the icon pointed out (on the taskbar or behind ^), the shortcut as a secondary tip, switches to keep the icon visible and add SCP to the Start menu where they apply, and "Show something now", which opens the picker.

### 4.4 Reliability

- If the TV is unplugged, the output closes silently and SCP returns to the tray; replugging restores it.
- A crash in a capture engine must not crash the app; it falls back to the other engine or the idle screen.
- Survives sleep and resume, display resolution changes and GPU driver resets.

### 4.5 Privacy and security

- Nothing except the chosen source ever appears on the output.
- Toast notifications and SCP's own UI never appear on the output.
- **(rev 2) The audience never sees SCP or what it is doing.** Every SCP top-level window except the output (picker, palette, badge, outline, menus, tooltips, drop-downs, settings, first run) is excluded from capture at creation; a capture guard applies this to any window the UI framework creates on its own, and an automated audit checks it. SCP UI never opens on the output monitor. SCP never uses Windows notifications (they can appear on any screen); messages go to the badge. Shortcuts never move focus or the mouse, so the presented app does not change state when the presenter uses SCP.
- **(rev 2)** If the TV duplicates (mirrors) the presenter's screen, SCP warns the user to switch to Extend (Win+P), because in that mode the audience sees everything.
- No network calls, no telemetry, no crash upload unless the user opts in. **(rev 3)** The one exception is user-initiated: "Set up a Zoom screen" runs Windows' winget, which downloads the driver. **(rev 5)** And the opt-in update check (FR-73), from a separate helper process.
- **(rev 5)** Never-show apps never reach the audience (FR-62). Marks for the audience never reveal private work: no click rings, pointer ring or laser while frozen or over another window, and a countdown never brings the picture back on its own. A slideshow's switch never undoes a blank, freeze or hold that came meanwhile, and a hold is checked again just before it lifts. Every new overlay (the click layer, the drawing layer and its toolbar) is hidden from capture and kept off the TV.
- **(rev 6)** Hidden parts (FR-74) are covered before anything is seen, on every engine, at every brightness, while frozen, and in screenshots and recordings of that window; something with hidden parts is never shown by an engine that can't cover them. The audience view, the text box for text marks and every form (a Wi-Fi password is typed in one) are hidden from capture. Live captions listen only while on, on the PC itself, and never show on a break screen or on black. A QR code's label never shows a Wi-Fi password, and the password is never saved.
- **(rev 9)** Password boxes are covered where Windows says they are, not where the picture seems to show them; every other picture on the TV (a pane, the small picture) is covered like the main one; faces are found and blurred on the PC, never recognised or kept; the check's preview is private and deleted at once; shortcuts on the TV never show typing; the replay keeps only the TV's own (already covered) picture, only while on, and deletes its parts; voice commands and the caret tracker never keep what's said or typed; notes travel to the phone encrypted with its pairing key; calendars and pictures are read from files on the PC, never fetched; the organisation's rules can only add protection.
- **(rev 10)** A whole screen shown leaves out its taskbar and desktop icons and goes black while an Open or Save box is on it; a browser shows only its page; the presenter is told when a meeting app shares a whole screen. A part of a screen that is all taskbar is refused, never widened. Text is copied only from a picture with hidden parts and automatic hiding already covered. The speaker's screen and the whiteboard window are hidden from capture; PowerPoint's notes are read on the PC and never reach the TV. Clicker keys are off unless chosen, taken with RegisterHotKey (no hook) and only while something they can move is shown. Words for captions are taken out of Windows' speech dictionary again when captions stop.
- **(rev 8)** Keys and passwords are covered by their shape or their label; your own words are kept in the settings file (they have to be read to be found) and never sent anywhere; a line that can't be checked in time is covered whole rather than shown. A contact card shows only the name under the code. The phone's new buttons open nothing on the PC.
- **(rev 7)** The phone remote is off unless switched on and can't be reached from outside the local network; its pairing code is never on the TV; its key never crosses the network and is stored encrypted; commands are signed, can't be replayed, are limited in rate and limited to a fixed list (no settings, no quitting, no saving, nothing that opens a window). Automatic hiding reads the picture on the PC only, and a transcript is written only when switched on.
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

**macOS and Linux (rev 11, planned).** A shared core with an Avalonia front end and a capture helper for each system: section 9.3.

**Screen capture compatibility.** A full-screen flip-model surface may be shown by the GPU directly ("independent flip"). Modern capture (Windows Graphics Capture, Desktop Duplication — used by Zoom, Teams, OBS and SCP's own screenshots) sees it; legacy GDI screen grabs may not.

## 6. External interfaces

### 6.1 User interface

**(rev 4) Windows 11 look.** Icons come from Windows' own icon font (Segoe Fluent Icons; Segoe MDL2 Assets on Windows 10), so there are no image files and they match the system. Text follows the Windows 11 type ramp: Body 14 px, Caption 12 px, Subtitle 20 px, Title 28 px, Segoe UI Variable Text and Display, semibold where bold is asked for. On Windows 11 22H2 and later, Mica sits behind title bars and the Settings section list, and acrylic behind the quick-actions panel; content with text boxes and labels stays on a solid layer, because GDI-drawn text can't keep its transparency on a backdrop (surfaces on a backdrop are drawn into an alpha bitmap and alpha-blended). Windows 10, older Windows 11 and High Contrast get solid colours. Settings is laid out like Windows Settings: icons in the section list, a 28 px page title, small group headings, and rows with icons. Search and help and Settings pages enter with a 160 ms fade or slide, the quick-actions panel slides up; all motion is off when Windows' Animation effects are off, and nothing on the TV ever animates except the existing fades through black. **(rev 5)** And what the presenter asks for: the zoomed view following the pointer, click rings, the laser's trail.

Two themes **(rev 2)**: Light (the default), Dark, or Match Windows. Dark: background #0B0B0C, surfaces #17171A, text #E6E6E6, accent #4C8DFF. Light: background #F3F3F5, surfaces #FAFAFB, text #1B1B1F, accent #2563D9. Rounded corners, Segoe UI Variable, no pure white anywhere. The output and idle screen are always #0B0B0C. Approved mockup: the "ScreenCast Presenter UI" canvas (picker, badge, tray menu, first run, outline, shortcuts, command palette).

| Screen | Contents |
| --- | --- |
| Tray icon | Grey when idle, accent dot when presenting; left-click opens the picker, right-click opens the menu. **(rev 4)** Left-click opens the quick-actions panel (FR-49) |
| Tray menu | **(rev 3)** Status; Show something… (Show “…” again); while presenting: Blank, Freeze, Show only part of it, Stop; Take a screenshot; Record… (recording controls while recording); Open my recordings; Show on ▸; Favourites ▸ (once there are some); Search and help…; Settings…; Quit |
| Picker | Centred overlay on the presenter's screen: search box, grid of live window thumbnails, monitors row, output selector (TV / Zoom screen / Floating), "Crop…" button |
| Crop overlay | Dimmed source with a draggable rectangle and a size readout; Enter confirms, Esc cancels |
| Presenting badge | Small pill at the top of the presenter's screen: source name, Blank, Stop; auto-hides after 3 s |
| Settings | Tabs: General (start with Windows, cursor), Keyboard shortcuts, Output (monitor, dimming, idle screen text), Recording (folders, audio), Appearance, Advanced (capture engine, diagnostics) **(rev 2)**. **(rev 3)** General (start with Windows, icon by the clock, Start menu / uninstall, shortcuts, outline, now-showing bar, status pill, pointer), Where it shows (screen or window, keep on top, sharing in Zoom or Teams, Zoom screen setup, dimming, blank message), Recording & screenshots, Appearance (theme, text size), Advanced (how windows are shown, settings file, privacy, version). **(rev 11, from a user report)** A save made anywhere refreshes the open page only when something on it reads differently (the switch just flipped already shows its new state), after the click that caused it; then the page is replaced in one repaint, at the same place and with focus on the same control, without applying the window's backdrop again (it flickered and jumped) |
| Share guide **(rev 3)** | Three numbered steps with drawings; Done, Choose what to show, Share a whole screen instead… |
| Quick-actions panel **(rev 4)** | Acrylic panel above the clock: preview and status, tiles, footer (FR-49) |
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
| `DwmRegisterThumbnail` of the output window **(rev 6)** | The audience view (FR-75) and the "what's next" preview (FR-76) |
| Direct2D Gaussian blur effect, geometry realizations, DirectWrite **(rev 6)** | Blurred hidden parts, cached ink, the banner, captions and text marks |
| SAPI in-process recognizer (`SpInprocRecognizer`, `SpMMAudioIn`, dictation grammar) **(rev 6)** | Live captions, offline, in the `--captions` helper |
| `Windows.Media.Ocr.OcrEngine` **(rev 7)** | Automatic hiding, in the capture helper |
| Sockets (`TcpListener`), `System.Net.NetworkInformation` **(rev 7)** | The phone remote, only in the `--remote` helper |
| DPAPI (`ProtectedData`, current user) **(rev 7)** | The phone remote's pairing key at rest |
| `SendInput`, `PostMessage` (Page Down / Page Up) **(rev 7)** | Next and previous slide |
| UI Automation (`IUIAutomation`, raw COM) **(rev 9)** | Password boxes (FR-98), the focused field (FR-99) |
| `GetGUIThreadInfo` **(rev 9)** | The text cursor (FR-99) |
| `Windows.Media.FaceAnalysis.FaceDetector` **(rev 9)** | Faces (FR-100) |
| Media Foundation source reader to sink writer, passthrough **(rev 9)** | Joining the last minute (FR-106) |
| Monitor configuration (`dxva2`: `SetVCPFeature`, capabilities) **(rev 9)** | The TV over the cable (FR-107) |
| Registry `SOFTWARE\Policies\ScreenCastPresenter` **(rev 9)** | Rules from the organisation (FR-109) |

**(rev 11)** What replaces each of these on macOS and Linux is listed in section 9.5.

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
7. **M7 – More for presenters (rev 5).** FR-05, FR-43 and FR-62–FR-73. Exit: unit tests for every rule above; the real engine draws every mark on the GPU; the helper takes every new command end to end; the stress harness's `cli`, `extras` and `nevershow` scenarios pass; the manual checks in docs/TESTING.md.
9. **M9 – Running the room (rev 7).** FR-86–FR-90. Exit: unit tests for every rule above; real text recognition finds an address in a rendered picture, and the GPU scan covers it; the phone page's own SHA-256/HMAC agree with .NET and RFC 4231 (run in Node); the real remote helper serves, refuses replays and unlisted commands, and stops with the app; a busy frame within NFR-09g; the stress harness's `room` scenario passes; the manual checks in docs/TESTING.md.
11. **M11 – Fourteen more (rev 9).** FR-98–FR-111. Exit: unit tests for every rule above; a real window's password box found by Windows, followed as it moves and hides, and covered in a real screenshot; a real face found and blurred (a public-domain painting); split screen and pictures in the real engine; the check helper run for real on a real window; three real encoded parts joined, and the real recorder keeping and saving the last of its parts; notes encrypted in .NET and decrypted by the page's own script in Node and by the real helper end to end; NFR-09j met; the stress harness's `room` scenario (pictures, cards, split screen, shortcuts, notes, settings export, the last minute) passes.
10. **M10 – More from everything (rev 8).** FR-91–FR-97, and the rest of FR-16, FR-51 and FR-59. Exit: unit tests for every rule above; real text recognition covering a labelled password and a listed name in a rendered picture; the words reaching the capture, screenshot and recording helpers through their own parsers; the phone page's script pressing the new buttons against the real remote helper (run in Node); NFR-09i met; the stress harness's `room` scenario (with `--on`/`--off`, own words and a shuffled playlist) passes.
8. **M8 – Stage tools (rev 6).** FR-74–FR-85. Exit: unit tests for every rule above; QR codes decode with an independent decoder, including one read back from the GPU's own picture; hidden parts, the spotlight and highlighter overlaps checked pixel by pixel on the GPU; every new helper command round-trips through the helper's own parser; NFR-09e and NFR-09f met; the stress harness's `stage` and `speed` scenarios pass; the manual checks in docs/TESTING.md.

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
| Hidden parts **(rev 6)** | Hide part of an email window; switch, freeze, restart the helper, kill the helper, take a screenshot and a recording; watch the TV frame by frame during fades | Nothing under a box is ever seen, in any frame or file |
| QR **(rev 6)** | Scan the TV's QR code for a link and for Wi-Fi with an iPhone and an Android phone from 3 m | Both read it first time; the Wi-Fi joins |
| Speed **(rev 6)** | `StressHarness speed`, and the GPU speed tests | Within NFR-09e and NFR-09f, and the harness budgets |
| Phone remote **(rev 7)** | Pair an iPhone and an Android phone; capture the Wi-Fi traffic and resend a command; try from another network | Both pair and work; the resent command is refused; nothing answers from outside |
| Automatic hiding **(rev 7)** | Scroll an inbox and a CRM page with it on; watch the TV | Addresses and numbers covered within half a second; dates and amounts left alone |
| Keys and your words **(rev 8)** | Show a terminal with an `.env` file, a password manager's export and a CRM listing clients on your list; take a screenshot and record | Every key, labelled password and listed name covered on the TV and in both files; the labels and other text readable |
| Contact card **(rev 8)** | Scan the TV's contact card with an iPhone and an Android phone | Both offer to save the contact with the name, phone and email |
| Password boxes **(rev 9)** | Sign in to a website and a desktop app while showing them; click "show password" | The box is covered the whole time, on the TV, in a screenshot and in a recording |
| Faces **(rev 9)** | Show a camera on a room with three people; choose "Everyone but me" | The two smaller faces are blurred; the presenter's isn't |
| The TV over the cable **(rev 9)** | "Test the TV" on a monitor and on a TV; schedule it off at 18:00 | Each says what it supports; the monitor goes to standby on time |
| Voice **(rev 9)** | Say "Presenter, next slide" in a quiet and a noisy room; talk about "the next slide" without the wake word | The slide changes only when asked |

## 9. macOS and Linux (rev 11 plan)

This section is a plan; none of it is built yet. It sets out what a v2.0 for macOS and Linux would do, what it can't do, how the code splits, and the order to build in. It was written on the Windows development PC, with no Mac or Linux PC to try things on, so every system behaviour it relies on is either documented or listed in 9.9 as a question for the first milestone (P0), answered on real machines before anything else is built.

### 9.1 What doesn't change

- **Only the chosen source reaches the audience; privacy beats convenience.** Where a system can't give a guarantee that SCP gives on Windows, the feature is off or asks first. It never quietly gives weaker protection (FR-139, FR-144).
- No bright flashes (4.2): the output is black until its first frame and every change passes through black, measured the same way (NFR-16, NFR-19).
- Plain language and one word for a screen (4.3); every action reachable from the icon; the command line; the settings file's format (so "settings to a file and back", FR-109, works across systems).
- No network use except the existing opt-in exceptions; no telemetry, no account.
- Code in this repository stays MIT. Libraries must be MIT-compatible; GPL projects (OBS, ddcutil) are studied, never copied.

### 9.2 What each system allows

| Topic | Windows (v1.0) | macOS 14 or later | Linux on X11 | Linux on Wayland |
| --- | --- | --- | --- | --- |
| Seeing other apps' windows (list, titles, positions, changes) | Yes | Yes (titles need Screen Recording permission) | Yes | No: SCP gets only what the presenter picks in the desktop's own picker |
| Capturing a window or screen | WGC and DWM, no permission | ScreenCaptureKit, with Screen Recording permission. macOS 15 and later ask the presenter from time to time to confirm it | XComposite and XShm, no permission | The ScreenCast portal and PipeWire. The desktop asks each time, unless it gave a restore token |
| Leaving things out of SCP's own capture of a whole screen | SCP's own windows | Any app or window, through ScreenCaptureKit's content filter | No (SCP can hide its own windows briefly for a screenshot) | No |
| Keeping SCP's windows out of other apps' captures (a meeting sharing the presenter's whole screen) | Yes (`WDA_EXCLUDEFROMCAPTURE`) | No: from macOS 15 a window's "don't share" setting is no longer honoured by ScreenCaptureKit, and Apple has said there is no public way to prevent capture | No | No |
| Global shortcuts | `RegisterHotKey` | `RegisterEventHotKey`, no permission | `XGrabKey` | The Global Shortcuts portal (KDE Plasma, and GNOME by version 49). The desktop shows the keys and the presenter confirms them |
| An icon by the clock | Notification area | Menu bar. On a MacBook with a notch, a full menu bar can hide it | StatusNotifierItem (KDE and most desktops; GNOME only with the AppIndicator extension, which Ubuntu turns on) | Same as X11 |
| Sending keys to a window in the background | `PostMessage` | `CGEventPostToPid`, with Accessibility permission | Only reliable into the focused window (many apps ignore synthetic keys) | No (the RemoteDesktop portal types only into the focused window, after the presenter agrees) |
| Reading another app's controls (password boxes, a browser's page, the focused field) | UI Automation | Accessibility API, with Accessibility permission | AT-SPI over D-Bus (apps with accessibility turned on) | AT-SPI works, but its answers can't be tied to the picked window or to screen positions |
| The pointer and clicks | Yes | Position yes; clicks through a global mouse monitor | XInput2 | Position only while it's over the shown source (the stream's pointer data); no clicks |
| Placing SCP's own windows | Yes | Yes | Yes | The TV output: full screen on a chosen screen. Other windows through XWayland (P0, Q11) |
| Pop-up notifications | Shown on the main screen | Shown on the main display (the one with the menu bar in Displays settings) | Depends on the desktop | Depends on the desktop |

### 9.3 Architecture

Today the app is about 42,000 lines. About 9,700 of them (most of Core, plus the pure parts of recording, the remote and the capture protocol) have no Windows reference. Another 3,200 lines in Core are tied to Windows only through a few calls: the session's DWM flush and monitor type, the phone remote's key storage, the organisation's registry rules, and the hotkey manager. The rest is Windows through and through: the UI (9,900), the app's partial classes (9,200), capture (6,200), interop (1,900) and recording (1,600). macOS and Linux each get their own version of that part, written fresh rather than translated.

```text
                    ScreenCastPresenter.Core   (net10.0, every system)
   settings · favourites · scenes · playlists · schedules · calendar · session controller
   command line · helper protocol · QR codes · GIFs · captions text · rehearsal · hiding rules
   tray menu, quick panel and help plans · interfaces for each system's services
            |                              |                               |
   Windows (v1.0, unchanged)       macOS                           Linux
   WinForms UI                     Avalonia UI  <-- shared -->     Avalonia UI
   DWM / WGC helper                helper: ScreenCaptureKit        helper: PipeWire portal (Wayland)
   Direct2D marks                  Skia stage   <-- shared -->     or XComposite (X11); Skia stage
   Media Foundation, SAPI, OCR     Swift shim: AVFoundation,       GStreamer, Tesseract, D-Bus
                                   Vision, Speech, Accessibility   (portals, AT-SPI, Secret Service)
```

| Project | What it holds |
| --- | --- |
| `src/ScreenCastPresenter.Core` (net10.0) | The portable code above, with the 3,200 lines cut free. Each system service becomes an interface: screens, windows, hotkeys, key storage, start at login, organisation rules, folders, the single-instance channel, the clipboard and slide keys. Its tests run on all three systems |
| `src/ScreenCastPresenter` (Windows) | Today's app, now referencing Core. Behaviour, tests and memory budgets unchanged |
| `src/ScreenCastPresenter.Desktop` (Avalonia) | For macOS and Linux: the icon and its menu, quick panel, picker, Settings, messages, first run, audience view, speaker screen, notes and whiteboard windows. Built from the same plans the Windows UI uses (TrayMenuPlan, QuickPanelPlan, HelpTopics, PickerLayout), so a new feature's tray line is still written once |
| `src/ScreenCastPresenter.Stage` (SkiaSharp) | For macOS and Linux: draws the picture and everything on it (fades, dimming, night filter, hidden parts and blur, spotlight, zoom, ink, arrows, the banner, captions, the timer, QR codes, the logo, the small picture and its shape, split screen). It takes the same one-line commands as the Windows helper (CaptureProtocol), so the session can't tell the systems apart |
| `native/mac` (Swift package, built to `libscp_mac.dylib`) | A small C interface for .NET over ScreenCaptureKit, AVFoundation (recording, playback, cameras), Vision (text, faces), Speech (captions), Accessibility and PowerPoint/Keynote scripting. Built on a Mac (a CI runner) |
| Linux backends (C#, no native code of ours) | D-Bus through Tmds.DBus.Protocol (portals, AT-SPI, notifications, Secret Service), GStreamer through its C API (PipeWire capture, encoding, playback, cameras), Xlib and XComposite for X11, PDFium, Tesseract |

Helpers stay helpers. As on Windows, the capture helper is the same program started with `--capture-host`, so a crash in capture never takes the app down (4.4). On macOS and Linux the helper owns the output window itself, because neither system lets one process's window sit inside another's (the Windows helper draws into a child window). `--remote`, `--captions`, `--pdf-pages`, `--read-text` and `--make-gif` keep their roles.

Choices and why:

- **Avalonia instead of a native UI for each system (SwiftUI, GTK):** one UI for two systems, in C# like the rest, MIT licensed. It draws its own controls, so SCP's web-like light look is the same on both. The icon's menu stays a native menu.
- **Windows keeps WinForms for now:** it works, it's tested, and it meets the memory budgets. Whether Windows moves to Avalonia later is decision D1.
- **A Swift shim instead of .NET's macOS bindings:** ScreenCaptureKit and Speech are asynchronous, delegate-based APIs. They are short and safe to call in Swift, and long and fragile through raw Objective-C calls. The shim's C interface stays small: start and stop a stream, take a frame, plus a few calls each for recording, text, faces and speech.
- **PDFium on macOS and Linux:** one renderer, so pages look the same on both (FR-115).

### 9.4 Each feature on each system

**Same**: works as on Windows. **Changed**: works, in a different way (see the notes). **Partly**: some of it works, as the notes say. **No**: can't be done on that system; SCP says so where the feature would be (FR-144).

| Feature | macOS | Linux X11 | Linux Wayland | Notes |
| --- | --- | --- | --- | --- |
| Window and screen list with live pictures, search (FR-01–FR-03) | Same | Same | Changed | macOS: pictures are refreshed about once a second while the picker is open (no zero-copy thumbnails). Wayland: "Choose…" opens the desktop's picker; things picked before are listed by their restore tokens |
| Click a window to show it (FR-05) | Same | Same | No | Wayland: the desktop's picker instead |
| What's shown closes or is minimised (FR-06, UC-06) | Same | Same | Same | A minimised window can't be captured on any system: its last picture stays and the presenter is told |
| The TV output, fades, idle screen, blank, freeze, break countdown, switch backlog (FR-10, FR-10a, FR-13, FR-14, FR-16, FR-20, FR-22, FR-27, FR-27a, FR-28, FR-66) | Same | Same | Same | macOS: a borderless window above the menu bar on the TV, never macOS's own full screen (that slides in and makes a new Space). Wayland: full screen on a chosen screen is the one placement a Wayland app may ask for |
| The output in a window (FR-11) | Same | Same | Partly | Wayland: "Keep the window on top" depends on the desktop |
| A virtual "Zoom screen" (FR-12) | No | No | No | There's no public way for an app to add a screen. A virtual screen made by another app is used like a TV; otherwise meetings share the output window |
| Status pill, now-showing bar, quick panel, Search and help, shortcut sheet, first run (FR-15, FR-19a, FR-25, FR-26, FR-49) | Same | Same | Changed | Wayland: placed and kept on top through XWayland (Q11) |
| The outline around what's shown (FR-17) | Same | Same | No | macOS: follows the window from the window list (Q4). Wayland: only the desktop's own "sharing" indicator |
| Identify screens (FR-18) | Changed | Same | Same | macOS has no screen numbers, so SCP numbers them (FR-146) |
| The icon and its menu (FR-20, FR-48) | Changed | Changed | Changed | See FR-141 |
| Global shortcuts (FR-21, FR-21a) | Changed | Same | Partly | See FR-142 |
| Favourites, previous source (FR-23, FR-41) | Same | Same | Changed | Wayland: a favourite keeps the picker's restore token. When the desktop won't restore it, the picker opens with a line saying which favourite it's for |
| The pointer on the TV (FR-24) | Same | Same | Same | ScreenCaptureKit and the portal both draw it or leave it out, as asked |
| Browsers keep to the page chosen (FR-24a) | Same | Same | No | Wayland: the picked window's title isn't given to SCP |
| One copy running, command line, `--on`/`--off`, `--status`, `--is` (FR-29, FR-43, FR-91, FR-129) | Same | Same | Same | A socket in the user's own runtime folder that only they can open replaces the named pipe. The command is `screencast-presenter` |
| How it captures (FR-30–FR-33) | Changed | Changed | Changed | macOS: ScreenCaptureKit only. There is no zero-copy way (FR-30) and no capture border to remove (FR-33); macOS shows its own recording indicator in the menu bar, never on the output. X11: XComposite. Wayland: PipeWire |
| Settings file, appearance, text size (FR-40, FR-44–FR-46) | Changed | Changed | Changed | Same file format, kept in `~/Library/Application Support` on macOS or `$XDG_CONFIG_HOME` on Linux. Appearance follows the system's light or dark setting. Accent colour: SCP blue or the system's |
| Start at login, install (FR-42, FR-47) | Changed | Changed | Changed | macOS: a login item (SMAppService), and installing means dragging the app to Applications from a signed DMG. Linux: an autostart entry (the Background portal in Flatpak), installed from Flathub or a tarball |
| Screenshots (FR-50, FR-51, FR-56) | Same | Changed | Changed | macOS leaves SCP's own windows out of its pictures. X11: SCP hides its own windows for the moment the picture is taken. Wayland: from a stream SCP already has, otherwise the Screenshot portal (the desktop may ask) |
| Recording with sound, pausing, crash-safe files, background recording (FR-52–FR-55, FR-57, FR-59–FR-61) | Same | Same | Partly | macOS: H.264 from the hardware encoder through AVFoundation, as fragmented MP4, with computer sound and the microphone from ScreenCaptureKit. Linux: GStreamer (VA-API hardware encoding, otherwise OpenH264), as fragmented MP4, with sound from PipeWire. Wayland: recording a covered window in the background depends on the desktop (Q7) |
| Only the recorded app's sound (FR-58) | Same | Partly | Partly | macOS: ScreenCaptureKit's filter gives one app's sound. Linux: PipeWire can record one app's stream when SCP knows which app it is (by its process on X11). On Wayland the picked window can't be tied to an app, so it records all computer sound |
| Never-show list (FR-62) | Changed | Same | No | macOS: never-show apps are left out of a whole screen by macOS itself (the audience sees what's under them, never them), and they can't be chosen. X11: as on Windows, black at once. Wayland: SCP can't tell which app the picker gave it or what's on a screen (FR-139) |
| Do not disturb reminder (FR-63) | Changed | Changed | Changed | macOS: the reminder's button opens Notifications settings ("Allow notifications when mirroring or sharing the display"). Linux: where the desktop lets an app turn on do not disturb, SCP turns it on while a whole screen is shown and off after; otherwise it reminds (Q8) |
| Zoom, click rings, pointer ring, spotlight, laser (FR-64, FR-65, FR-71's laser, FR-77) | Same | Same | Partly | Wayland: the stream says where the pointer is over what's shown, so zoom, the pointer ring, the spotlight and the laser work. Clicks can't be seen, so there are no click rings |
| Zoom that follows typing (FR-99) | Changed | Changed | No | macOS: the focused field from Accessibility (permission). X11: AT-SPI's caret. Wayland: no screen positions |
| Drawing, arrows, boxes, text (FR-71, FR-78) | Same | Same | Changed | Wayland: drawn on the audience view (FR-75) instead of over the window, because SCP doesn't know where the window is. The other systems offer this as an option too |
| Talk timer, time budgets, a timer on the TV, the speaker's screen (FR-67, FR-120, FR-121, FR-124) | Same | Same | Same | |
| Cameras (FR-68) | Same | Same | Same | macOS: AVFoundation (Camera permission; an iPhone used as Continuity Camera is listed too). Linux: PipeWire's camera portal, or V4L2 through GStreamer |
| Slideshow, rooms, schedules, scenes, playlists, shuffle, calendar (FR-69, FR-82, FR-83, FR-86, FR-87, FR-95, FR-97, FR-108) | Same | Same | Partly | Shared core. Wayland: items that need a pick (windows, screens) are restored from their tokens, or skipped like an app that isn't open |
| A TV plugged in; a TV that copies the screen (FR-70, 4.5) | Same | Same | Same | Each system's UI framework reports screen changes |
| The small picture and its shape, split screen, a second TV (FR-72, FR-85, FR-104, FR-118) | Same | Same | Partly | Wayland: each extra window or screen is a separate pick; a whole screen beside the main picture follows FR-139 |
| Update check (FR-73) | Same | Same | Same | For the DMG and the tarball; Flathub updates its own apps |
| Crop, hide part of it (FR-04, FR-74) | Same | Same | Changed | Wayland: the box is dragged on a picture of the source in an SCP window. It's kept as fractions of the window, as on Windows, so it still follows the window |
| The audience view, get the next thing ready (FR-75, FR-76) | Same | Same | Changed | The helper draws a second, private copy of the output. Wayland: the next thing is picked in the desktop's picker |
| Name banner, logo, QR codes, contact card, night filter (FR-79–FR-81, FR-94, FR-123) | Same | Same | Same | Drawn by the shared Skia stage |
| Live captions, transcripts, subtitles, words for captions, voice commands, rehearsals (FR-84, FR-89, FR-96, FR-110, FR-127, FR-128) | Changed | Partly | Partly | macOS: Apple's on-device speech recognizer (Microphone and Speech Recognition permissions), with the listed words as its custom vocabulary. Linux has no built-in recognizer (decision D4) |
| Automatic hiding, keys, your own words, copying text, the check before sharing (FR-88, FR-92, FR-102, FR-130) | Same | Changed | Changed | macOS: Vision's text recognition, on the Mac. Linux: Tesseract, on the PC. It's slower, so the 0.4 s rhythm and the 2 s first-look wait may grow (Q10) |
| Phone remote, notes on the phone (FR-90, FR-93, FR-111) | Same | Same | Partly | The pairing key lives in the Keychain or the Secret Service (FR-143). Wayland: slide buttons as in the clicker row below |
| Password boxes covered exactly (FR-98) | Changed | Changed | No | macOS: Accessibility's secure text fields (permission; Chromium browsers are asked to turn on their accessibility). X11: AT-SPI's password fields. Wayland: answers can't be tied to the picked window |
| Faces blurred (FR-100) | Same | Partly | Partly | macOS: Vision. Linux has no built-in face detector (decision D5) |
| Shortcuts on the TV (FR-101) | Changed | Same | No | macOS: needs Input Monitoring permission. Wayland: keys pressed in other apps can't be seen |
| Pictures, cards, PDFs, the whiteboard, the room sign (FR-103, FR-115, FR-117, FR-125) | Same | Same | Same | PDFium makes the pages on macOS and Linux |
| Videos (FR-116) | Partly | Same | Same | macOS's own player opens MP4, M4V and MOV; WMV, AVI, MKV and WebM need another decoder (Q10). Linux: GStreamer plays them all, with the Flatpak runtime's codecs |
| Next and previous slide sent to what's shown, clicker keys (FR-90, FR-110, FR-132) | Changed | Partly | No | macOS: keys posted to the app (Accessibility permission; Q5). X11: dependable only while the shown window has focus. Wayland: keys go only into the focused window, so clicker keys and the phone's slide buttons work only while the presenter is in the shown window, and Settings says so. On every system, SCP turns PDF pages and moves videos itself |
| Chapters, timeline, the last minute, GIFs (FR-105, FR-106, FR-131) | Same | Same | Same | AVFoundation or GStreamer join and read the MP4 files; the GIF encoder is SCP's own |
| The TV over the cable (FR-107) | Partly | Changed | Changed | macOS: Apple silicon only through private interfaces (as the MonitorControl app does), which a macOS update can break; Intel Macs through I²C. Linux: the i2c-dev driver; the presenter must be allowed to use it (the `i2c` group), and SCP explains how |
| Organisation rules, settings to a file (FR-109) | Changed | Changed | Changed | macOS: a configuration profile (managed preferences). Linux: `/etc/screencast-presenter/policy.json`, owned by root |
| The taskbar left out (FR-112) | Changed | Same | No | macOS: the menu bar and the Dock are left out (the screen's visible area); a Dock that hides itself is left out at its full size. X11: the work area and the panels' reserved edges |
| Desktop icons (FR-113) | Changed | Partly | No | macOS: Finder's desktop icons are left out of the picture by macOS itself, so nothing on the desktop changes (Q6). X11: only where the desktop's icon window can be identified |
| Open and Save boxes (FR-114) | Partly | Partly | No | macOS: sandboxed apps' boxes belong to a system service, which is left out of the picture like a never-show app (Q6); other apps' boxes are found with Accessibility. X11: file dialogs found by their window type and role, where the toolkit sets one |
| Browsers: only the page (FR-133) | Changed | Changed | No | macOS: the page's area from Accessibility (permission). X11: AT-SPI's document frame |
| A meeting sharing a whole screen (FR-134) | Same | Same | No | Meeting apps' windows from the window list |
| Follow me (FR-119) | Same | Same | No | macOS: the active app and its front window from the window list. X11: the active window property |
| PowerPoint's notes (FR-122) | Changed | No | No | macOS: PowerPoint and Keynote through AppleScript (Automation permission for each). Linux: LibreOffice Impress could come later |
| Connect a wireless TV (FR-126) | Changed | Partly | Partly | macOS: AirPlay from Control Center with "Use As Separate Display"; SCP opens Displays settings and says how. Linux: GNOME Network Displays, where installed |

Counted over these 55 rows: macOS has 32 Same, 19 Changed, 3 Partly and 1 No. Linux on X11 has 34 Same, 12 Changed, 7 Partly and 2 No. Linux on Wayland has 13 Same, 15 Changed, 11 Partly and 16 No. In short, macOS can do nearly everything, mostly at the cost of permissions. X11 can do almost as much. Wayland loses everything that needs SCP to see other windows.

### 9.5 What replaces each Windows interface

| Windows (6.2) | macOS | Linux |
| --- | --- | --- |
| `EnumWindows`, titles, cloaking | `SCShareableContent`, `CGWindowListCopyWindowInfo` | X11: `_NET_CLIENT_LIST_STACKING`, window names. Wayland: none (the ScreenCast portal's picker) |
| DWM thumbnails | ScreenCaptureKit stills (`SCScreenshotManager`) | X11: XComposite pixmaps. Wayland: none |
| WGC, Direct3D 11, the swap chain | ScreenCaptureKit stream (IOSurface frames) drawn by Skia on Metal | The ScreenCast portal and PipeWire (Wayland), XComposite and XShm (X11), drawn by Skia on OpenGL |
| Direct2D, DirectWrite, Gaussian blur | Skia (SkiaSharp) | Skia (SkiaSharp) |
| `EnumDisplayMonitors`, `WM_DISPLAYCHANGE` | `NSScreen` and its change notification (through Avalonia) | XRandR or Wayland outputs (through Avalonia) |
| `SetWindowDisplayAffinity` | `SCContentFilter` leaving SCP out (SCP's own captures only) | None |
| `RegisterHotKey` | `RegisterEventHotKey` | `XGrabKey`; the Global Shortcuts portal |
| `Shell_NotifyIcon` | `NSStatusItem` (Avalonia's TrayIcon) | StatusNotifierItem over D-Bus (Avalonia's TrayIcon) |
| Media Foundation sink writer, WASAPI and per-process loopback | `AVAssetWriter` (VideoToolbox), ScreenCaptureKit audio | GStreamer (`vah264enc` or `openh264enc`, `mp4mux`), PipeWire audio |
| `Windows.Media.Playback` | `AVPlayer` with `AVPlayerItemVideoOutput` | GStreamer `playbin` with an app sink |
| `Windows.Data.Pdf` | PDFium | PDFium |
| `Windows.Media.Ocr` | Vision (`VNRecognizeTextRequest`) | Tesseract |
| `FaceDetector` | Vision (`VNDetectFaceRectanglesRequest`) | Decision D5 |
| SAPI and its speech dictionary | Speech (`SFSpeechRecognizer`, on the device) with contextual strings | Decision D4 |
| UI Automation, `GetGUIThreadInfo` | Accessibility (`AXUIElement`) | AT-SPI over D-Bus |
| `SendInput`, `PostMessage` | `CGEventPostToPid` | XTest; the RemoteDesktop portal (focused window only) |
| PowerPoint through COM | AppleScript (PowerPoint, Keynote) | None |
| DPAPI | Keychain | Secret Service (over D-Bus) |
| Registry policies | Managed preferences (a configuration profile) | A file in `/etc` owned by root |
| The Run key, Start menu install | `SMAppService` login item; a DMG | XDG autostart or the Background portal; Flatpak |
| Named pipe | A Unix socket in `$TMPDIR`, mode 0600 | A Unix socket in `$XDG_RUNTIME_DIR`, mode 0600 |
| `dxva2` monitor configuration | Private IOAVService (Apple silicon), I²C (Intel) | `/dev/i2c-*` (as ddcutil does) |
| `SetThreadExecutionState` | `IOPMAssertion` | The Inhibit portal |
| `SHQueryUserNotificationState` | None (a reminder only) | The desktop's do not disturb, where offered |
| Cast (Win+K) | AirPlay (Control Center) | GNOME Network Displays |

### 9.6 Privacy on each system

- **macOS** is stronger than Windows in one way and weaker in another. Stronger: SCP's own captures can leave out whole apps. Never-show apps, SCP's own windows, Finder's desktop icons and sandboxed apps' Open and Save boxes are removed from a whole screen by macOS itself, instead of the picture going black. Weaker: from macOS 15, a window can't keep itself out of other apps' captures. If a meeting shares the presenter's whole Mac screen, it shows SCP's windows (picker, pill, notes) like any others. SCP never opens a window on the TV or over the output, so sharing the output window or the TV's screen, the way SCP is meant to be used (6.4), stays clean, and the share watch (FR-134) warns when a meeting shares the presenter's own screen. Notification banners appear on the main display, so first run checks that the TV isn't it, and the output sits above banners (Q2).
- **Linux on X11** is close to Windows, except that SCP's windows can't be kept out of other apps' captures (as on macOS).
- **Linux on Wayland:** the desktop decides what SCP may see. SCP can't keep never-show apps, Open and Save boxes, desktop icons or panels off a whole screen, and FR-139 decides what happens instead. A window shown on its own is as private as anywhere: the desktop sends that window and nothing else.

### 9.7 Requirements (rev 11)

| ID | Requirement | Priority |
| --- | --- | --- |
| FR-135 | One shared core: the portable code moves into `ScreenCastPresenter.Core` (net10.0) with no Windows reference, and each system service becomes an interface. The Windows app's behaviour, tests and memory budgets don't change. The core's tests run on Windows, macOS and Linux | Must |
| FR-136 | Permissions, explained first. Before macOS or a Linux portal asks, one SCP screen says what is needed, why, and what won't work without it: Screen Recording (needed to show anything), Accessibility (password boxes, a browser's page, Open and Save boxes, slide keys, zoom that follows typing), Input Monitoring (shortcuts on the TV), Microphone and Speech Recognition (captions, voice commands, rehearsals), Camera, and Automation (PowerPoint and Keynote notes). Each is asked for when first needed, except Screen Recording, which first run asks for. A permission later taken away is noticed and said. macOS's periodic Screen Recording question is explained before it first comes | Must |
| FR-137 | Capture on macOS with ScreenCaptureKit: a window on its own, which shows even when covered; a screen, with SCP, never-show apps, Finder's desktop icons and the Open/Save service left out by the content filter; a part of either, for crops; the pointer drawn or left out (FR-24). Frames stay on the GPU (IOSurface to Metal) | Must |
| FR-138 | Capture on Linux. On Wayland, through the ScreenCast portal: a window or screen picked in the desktop's picker, the pointer as data so SCP can draw it or leave it out, and the restore token kept so favourites and "Show it again" come back without asking where the desktop allows. On X11, windows and screens directly (XComposite, XShm, XFixes for the pointer), as on Windows. The session type decides which is used, and Settings › Advanced says which | Must |
| FR-139 | Whole screens on Wayland. SCP can't see what's on a screen there, so "Allow whole screens" is off by default (Settings › Privacy, with the reason in one line). When switched on, every time a whole screen is about to be shown SCP says which protections can't work on it (the never-show list, Open and Save boxes, desktop icons, the panel), with "Show it anyway" and "Show a window instead". The organisation can keep it off (FR-109). Windows, pictures, PDFs, videos, cameras and the whiteboard are unaffected | Must |
| FR-140 | The TV output on macOS and Linux belongs to the capture helper. It is borderless and full screen on the chosen screen (above the menu bar and the Dock on macOS, never a macOS full-screen Space) and black until its first frame, and it fades through black as on Windows (NFR-10 to NFR-16, measured the same way). The window output (FR-11) is an ordinary window that meetings can share | Must |
| FR-141 | The icon: in the menu bar on macOS, in the tray (StatusNotifierItem) on Linux. Where there's no tray (GNOME without the AppIndicator extension), opening the app again shows the quick panel, and first run says so. On a MacBook whose notch can hide it, first run checks that it shows and says what to do if not | Must |
| FR-142 | Shortcuts. macOS: the defaults use Control+Option instead of Ctrl+Alt (Command shortcuts belong to apps). Linux on X11: as on Windows. Wayland: through the Global Shortcuts portal where the desktop has one; otherwise Settings shows how to make a desktop shortcut that runs `screencast-presenter --action …`. Conflicts are reported as in FR-21a | Must |
| FR-143 | Per system. Settings are kept where the system keeps app settings, in the same format. Start at login (FR-42). One copy running, with commands through a socket only the user can open (FR-29, FR-43). The phone's pairing key goes in the Keychain or the Secret Service and is never stored in plain text; without a Secret Service, the phone remote stays off and says why. Organisation rules come from a configuration profile or a file owned by root | Must |
| FR-144 | What can't be done says so. A feature this system can't do is shown greyed in the menu, the quick panel, Search and help and Settings, with one line saying why (for example "Not on Wayland: ScreenCast Presenter can't see other windows here"). It is never left out silently. The command line exits with 1 and the same line, and `--status` lists it as unavailable | Must |
| FR-145 | Packages. macOS: one signed and notarized universal app (Apple silicon and Intel) in a DMG. Linux: a Flatpak on Flathub, whose portals suit FR-138, and a tarball for X11 users. Nothing needs admin rights to install | Must |
| FR-146 | Screen numbers: macOS has none, so SCP numbers screens itself (the main display is 1, then left to right) and Identify (FR-18) shows them. Linux uses the order the desktop gives. One word for a screen everywhere: "Screen 2" | Should |

| ID | Requirement | Target |
| --- | --- | --- |
| NFR-17 | Memory on macOS and Linux | Set in P0 from measurements. Aim: idle under 60 MB, presenting under 150 MB for the app and helper together (a UI framework that draws its own controls starts larger than WinForms) |
| NFR-18 | CPU while presenting 1080p60 on a recent laptop | ≤ 5%, with frames kept on the GPU |
| NFR-19 | Flashes | Zero in 100 switches on each system, measured as in NFR-16 (FlashCheck made to run on macOS and Linux) |
| NFR-20 | Added latency | ≤ 2 frames |
| NFR-21 | Cold start to the icon | ≤ 1.5 s |

### 9.8 Order of building

Estimates assume one developer with a Mac, a Linux PC (GNOME and KDE Plasma, on Wayland and on X11) and a TV at hand. Most of the time goes into checking on real machines, not into writing code.

1. **P0 – Answers from real machines (2 weeks).** A throwaway test app on a Mac and on Linux answers the questions in 9.9: memory of an Avalonia tray app, a ScreenCaptureKit window fading in on a TV (checked with FlashCheck), a portal stream on GNOME and KDE, global shortcuts, notifications over the TV. Exit: every question answered in this section; a go or no-go for each system; NFR-17 to NFR-21 set.
2. **P1 – The shared core (2 weeks, on Windows).** FR-135. Worth doing even if the port stops here: the core is cleaner and testable on its own. Exit: the Windows app unchanged (the full test suite, the stress harness scenarios and FlashCheck as before); the core builds and its tests pass on macOS and Linux in continuous integration (GitHub Actions: Windows, macOS and Ubuntu runners).
3. **P2 – macOS: first show (5 weeks).** The Avalonia front end (menu bar icon, quick panel, picker, Settings, messages, first run with permissions), the Swift shim and the Skia stage. Showing a window or a screen on a TV or in a window, with fades, blank, freeze, stop, favourites, previous, shortcuts, the never-show list, the outline and the command line. FR-136, FR-137 and FR-140 to FR-146 for macOS. Exit: UC-01, UC-03 and UC-05 on a Mac with a TV; zero flashes in 100 switches; the icon works on a MacBook with a notch; a signed, notarized DMG runs on a clean Mac.
4. **P3 – Linux: first show (5 weeks).** The same on Linux: the X11 and Wayland backends, FR-138, FR-139 and FR-141 to FR-145 for Linux. Exit: the same use cases on GNOME (Wayland), KDE Plasma (Wayland) and an X11 session; whole screens refused on Wayland until allowed; the Flatpak installs and runs.
5. **P4 – Stage tools (4 weeks).** Marks, zoom, the spotlight, the laser, drawing (including on the audience view), the banner, the logo, QR codes, the timer, the night filter, the small picture, split screen, hidden parts, the audience view, the next thing ready and a second TV. Exit: the GPU pixel tests run against the Skia stage on macOS and Linux (nothing under a hidden part in any frame; a busy frame within 16 ms).
6. **P5 – Recording and media (4 weeks).** Screenshots, recording with sound, background recording, the last minute, chapters, GIFs, cameras, pictures, PDFs, videos, the whiteboard and the room sign. Exit: FR-52 to FR-61 where the table allows; an 8-hour recording plays after the power is cut.
7. **P6 – Privacy and the room (5 weeks).** Automatic hiding, password boxes, faces, a browser's page only, Open and Save boxes, desktop icons, the taskbar, the share watch, Follow me, captions and voice commands (after D4), the phone remote, notes, PowerPoint and Keynote notes, clicker keys, the TV over the cable, organisation rules. Exit: the privacy, hidden-parts, automatic-hiding, keys, password-box, faces and phone-remote acceptance tests of 8.1, on each system that has the feature (9.4).
8. **P7 – Release (2 weeks).** An 8-hour soak test on each system, the README and docs/TESTING.md, the Flathub submission, the update check. Exit: every Must requirement in 9.7 passes, and every Same and Changed row in 9.4 works on its system.

That's about 29 weeks, roughly seven months. Continuous integration (from P1) builds all three systems and runs every automated test it can. It can't grant macOS's Screen Recording permission, run a Wayland desktop's picker or drive a TV. Those checks are manual, listed in docs/TESTING.md, and run on a real Mac and a real Linux PC at the end of each milestone (decision D2). The Windows development PC can build the C# for all three systems, but not the Swift shim, and it can't run anything on macOS or Linux.

### 9.9 Questions for P0

| # | Question |
| --- | --- |
| Q1 | Does ScreenCaptureKit keep capturing a window that is covered or on another Space, and how soon does the stream end when the window closes? |
| Q2 | Does a borderless window above the menu bar appear on the TV without a slide or a flash, and does it stay above notification banners and the Dock? |
| Q3 | How often do macOS 15 and later ask again about Screen Recording for an app that keeps a stream open? Does the system picker (`SCContentSharingPicker`) avoid the question, and at what cost to one-click showing? |
| Q4 | Can the outline be kept directly above another app's window on macOS, or must it stay on top of everything? How quickly are moves seen? |
| Q5 | Do PowerPoint and Keynote act on Page Down sent to their process while in the background? |
| Q6 | Does leaving out Finder's desktop-icon window keep the wallpaper? Does leaving out the Open and Save service hide sandboxed apps' boxes? |
| Q7 | On GNOME and KDE: does a window stream keep running while the window is covered or on another workspace? Do restore tokens bring back a window (not just a screen) without asking? Does pointer data arrive while the pointer is over the window? |
| Q8 | Which Linux desktops offer global shortcuts and let an app turn on do not disturb? Does the tray icon show on Ubuntu, Fedora (GNOME) and KDE? |
| Q9 | Memory and CPU: an idle Avalonia tray app, and the helper at 1080p60, on each system. |
| Q10 | Tesseract's speed on a 1080p frame (FR-88's rhythm); the speed and accuracy of the D4 candidates on a typical laptop; a decoder for WMV, AVI, MKV and WebM on macOS. |
| Q11 | Avalonia's own Wayland backend (added in 12.1, still marked experimental) compared with XWayland: placing SCP's windows, keeping them on top, the tray, and the licence that covers it. |

### 9.10 Decisions for the owner

| # | Decision | Options |
| --- | --- | --- |
| D1 | The Windows UI | Keep WinForms (recommended until macOS and Linux catch up; meanwhile two UIs share the menu, panel and help plans), or move Windows to Avalonia too (one UI, but the tested Windows app is rebuilt) |
| D2 | Test machines | Who tests on a Mac and a Linux PC with a TV at the end of each milestone (the development PC can't) |
| D3 | Accounts | Apple Developer Program membership (needed to sign and notarize; without it macOS blocks the app by default) and a Flathub account |
| D4 | Captions on Linux | A local open-source recognizer with a model the presenter installs (whisper.cpp: more accurate, heavier; Vosk: lighter), or no captions, voice commands or rehearsal transcripts on Linux |
| D5 | Faces on Linux | Bundle a small face-detection model and its runtime (several MB), or keep face blurring to Windows and macOS |
| D6 | Which first | macOS first (recommended: closest to Windows, with far fewer No rows), Linux first, or only one of them |

## Appendix A. Open-source references

| Project | Why study it | Language | Licence |
| --- | --- | --- | --- |
| [OnTopReplica](https://github.com/LorenzCK/OnTopReplica) | Closest existing tool: mirrors any window with DWM thumbnails, region crop | C# | MS-RL (changed files must stay open source) |
| [Win32CaptureSample](https://github.com/robmikh/Win32CaptureSample) | Reference code for WGC window and monitor capture in Win32 | C++ | MIT |
| [Magpie](https://github.com/Blinue/Magpie) | Lightweight fullscreen mirroring of a window using WGC, multi-monitor | C++ | GPL-3.0 |
| [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) | Virtual monitor for the "Zoom screen" output; installable via winget | C++ | MIT |
| [OBS Studio](https://github.com/obsproject/obs-studio) | Window capture and fullscreen projector patterns (heavy; study, don't copy) | C/C++ | GPL-2.0 |

**(rev 11)** For the macOS and Linux plan (section 9):

| Project | Why study it | Language | Licence |
| --- | --- | --- | --- |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | The UI for macOS and Linux | C# | MIT (check its Wayland backend's, Q11) |
| [PDFium](https://pdfium.googlesource.com/pdfium/) | PDF pages on macOS and Linux | C++ | BSD-3-Clause |
| [Tesseract](https://github.com/tesseract-ocr/tesseract) | Text recognition on Linux | C++ | Apache-2.0 |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) and [Vosk](https://github.com/alphacep/vosk-api) | Offline captions on Linux (D4) | C++ | MIT; Apache-2.0 |
| [MonitorControl](https://github.com/MonitorControl/MonitorControl) | DDC/CI on Apple silicon Macs | Swift | MIT |
| [ddcutil](https://github.com/rockowitz/ddcutil) | DDC/CI on Linux | C | GPL-2.0 (study only) |
| OBS Studio's `mac-capture` and `linux-pipewire` plugins | ScreenCaptureKit and portal capture in practice | C, Objective-C | GPL-2.0 (study only) |

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

**(rev 11, planned)** For macOS and Linux (section 9.3): Avalonia for the UI, SkiaSharp for the stage, a Swift package with a C interface for macOS's frameworks, Tmds.DBus.Protocol for Linux's portals, AT-SPI and Secret Service, GStreamer for Linux media, PDFium and Tesseract. Published as a signed, notarized universal app in a DMG, and as a Flatpak.
