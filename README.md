<p align="center">
  <img src="docs/assets/logo.png" width="200" alt="WoWWindowKit — a gold-framed main window with four blue companion panes">
</p>

<h1 align="center">WoWWindowKit</h1>

<p align="center">
  <strong>Your party. Your windows. One place to take the lead.</strong><br>
  Arrange your WoW clients, switch accounts, and focus windows with your mouse.
</p>

<p align="center">Windows x64 · .NET 10 · 5-man &amp; 10-man layouts · Dark interface</p>

<p align="center">
  <a href="#quick-start">Quick start</a> ·
  <a href="#layouts-and-swapping">Layouts</a> ·
  <a href="#everyday-controls">Controls</a> ·
  <a href="#configuration">Configuration</a> ·
  <a href="#build-and-test">Build &amp; test</a>
</p>

---

Keep one client in the large main slot and the rest within reach. Press an account's
hotkey to bring it forward, or move your pointer over an exposed game window to
focus it. Account numbers stay the same as windows change position.

| Feature | What it does |
| --- | --- |
| **Ready-made layouts** | Arrange five or ten clients, with editable coordinates and borderless mode. |
| **Account hotkeys** | Select a specific account with F1–F5 or F1–F10 by default. |
| **Mouse focus** | Focus the managed WoW window under your pointer; leave other apps alone. |
| **Live controls** | See assignments, catch or forget clients, switch profiles, and pause from a compact panel or the tray. |
| **Restore on exit** | Return managed windows to their original styles and positions. |

WoWWindowKit manages windows and focus. It does not move the mouse, inject clicks,
broadcast input, or automate gameplay. It does not change Windows' global
active-window tracking setting.

## Quick start

Build from source on Windows with the **.NET 10 SDK**:

```powershell
git clone https://github.com/David-c0degeek/WoWWindowKit.git
cd WoWWindowKit
.\build.ps1 -Portable
& ".\dist\Start 5-man.cmd"
```

The portable build includes the runtime. If you already have a built `dist` folder,
start with the launchers below.

1. **Exit OpenMultiBoxing / WowOpenBox if it is running.** Its normal exit also
   disables its Windows-wide mouse tracking, so the two apps do not compete.
2. **Choose your party size.** Run `Start 5-man.cmd` or `Start 10-man.cmd` in `dist`.
3. **Start your WoW clients normally.** They attach automatically in process-start
   order. Existing `WoW 1` … `WoW 10` titles retain their numbers where possible.
4. **Pick your main.** With a managed WoW window active, press F1–F5 or F1–F10.
   Move the pointer between exposed game windows to change focus.

> **First launch:** the display name defaults to **Notepad++**. Change it in
> **Settings & layout → App name**. Look for that name in the controls and tray.
> The bundled layouts target a 2560 × 1440 desktop; adjust their coordinates in
> Settings for your own display.

## Layouts and swapping

Layouts use physical desktop pixels:

| Profile | Main window | Companion windows |
| --- | --- | --- |
| **5-man** | 1880 × 1392 at (0, 0) | Four down the right at x = 1880 |
| **10-man** | 2048 × 1152 at (0, 0) | Four down the right, five along the bottom |

An account's number is its identity, independent of its current position. Select
account **N** and it moves to the main slot; account **1** moves to slot **N**;
the previous main returns to its numbered slot. Selecting account **1** restores
the numbered layout.

Edit coordinates and borderless mode in **Settings & layout**, or manually move
and resize your clients and save their current positions from the tray menu.
Switching to 5-man restores extra clients and excludes them from focus and
hotkeys. Switching back reuses their account numbers.

### Mouse focus

Hover focus works only over an exposed, managed WoW window. Move over the desktop,
another app, or a window covering WoW and focus changes stop. Return to a managed
game window and they resume.

Focus pauses while a mouse button is held or Settings is open. After a hotkey
swap, a stationary pointer leaves the chosen account active; the first mouse
movement resumes hover focus. Mouse transitions are event-driven, with a periodic
fallback for windows appearing beneath a stationary pointer.

## Everyday controls

The compact dark control window opens on startup. It shows each slot, its hotkey,
the assigned client's PID and title, and the current main account. Management
continues while it is visible. The tray icon's right-click menu provides the
same core actions.

| Action | Where / how |
| --- | --- |
| Bring an account to main | F1–F5 / F1–F10 while a managed WoW window is active, or **Make main** in controls |
| Toggle mouse focus | Ctrl+Shift+F while WoW is active, or the controls / tray toggle |
| Assign a running client | Select a slot and window, then **Catch / assign** |
| Release a client | **Forget** restores its original borders and position |
| Change profile, pause, or reapply layout | Controls or tray menu |
| Edit layout, hotkeys, or app name | **Settings & layout**, or `Settings.cmd` |
| Reopen controls | Double-click the tray icon, **Show controls**, or `Controls.cmd` |
| Restore windows and quit | **Exit** in controls or the tray menu |

Closing the control window or choosing **Hide to tray** keeps the app running.
Use `WoWWindowKit.exe --tray` on first launch to start hidden. Only one instance
runs per Windows session: another profile launcher switches the existing instance,
and another Settings launch opens its editor.

Swap hotkeys can be edited in Settings: use F1–F12, optionally with Ctrl, Shift,
or Alt. Unbound combinations such as Ctrl+F1 have no special behavior. Bound
keys pass through normally when another application is active.

### Replacing and reassigning clients

A closed client leaves its account number empty. A new client normally fills an
empty number automatically; **Catch / assign** corrects ambiguous launch order or
assigns a replacement after a crash. Window choices show PID and assignment status,
with the newest client first and labelled **Newest**.

- Catching an outside client into an occupied slot releases and forgets the old client.
- Choosing a client already assigned elsewhere exchanges the assignments.
- Catching a client returns the layout to numbered positions, with account 1 as main.
- Forgotten clients stay excluded from discovery, hotkeys, and mouse focus until
  explicitly caught again or the app restarts.

Assignments and forgotten-client exclusions last for the current app session.
They are not saved as persistent account identities.

## Configuration

`settings.json` is created beside the executable on first launch using
[`settings.default.json`](settings.default.json). Keep the application folder
writable. Saves are validated and atomic, and the previous version is kept as
`settings.json.bak`. Restart the app after editing the JSON manually.

| Setting | Behavior |
| --- | --- |
| **App name** | Changes control / Settings titles, tray label, and application dialogs on save; game titles are unaffected. |
| **Profiles** | Each profile stores window coordinates and account swap keys. |
| **ProcessNames** | Exact allowed executable names, without `.exe`: `Wow`, `WowClassic`, and `WowClassicT` by default. |
| **MousePollMs** | Fallback mouse-focus polling interval; defaults to 50 ms. |

You can rename `WoWWindowKit.exe` while it is closed; update the four generated
`.cmd` launchers to use the new filename. Settings are located relative to the
executable regardless of its name. Windows Task Manager may show the executable
name or embedded product metadata separately from the configured app name.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| **Windows refuses focus changes** | Click a WoW window once and retry. Run WoWWindowKit at the same privilege level as WoW; it does not request elevation automatically. |
| **A client has the wrong number** | Use **Catch / assign** to select the intended account slot. |
| **A client is not discovered** | Check `ProcessNames` and whether the client was forgotten during this session. |
| **The app seems to have disappeared** | Check Windows' hidden tray icons, or run `Controls.cmd`. Closing controls hides the panel. |
| **Settings fail to load** | Inspect `settings.json` and the previous save in `settings.json.bak`; check `app.log` beside the executable. |
| **Windows remain borderless after a crash** | Relaunch the app and exit normally, or restart the game clients. |

Foreground activation uses Windows APIs, with a temporary input-thread attachment
fallback. Threads are detached immediately afterward, and hung windows are skipped.
Actual focus behavior and rendering after a resize depend on the game and the
Windows session.

## Build and test

Requires **Windows x64** and the **.NET 10 SDK**. There are no third-party packages.

```powershell
.\test.ps1                 # Integration checks using ten separate mock clients
.\build.ps1                # Smaller build; requires .NET 10 Windows Desktop runtime
.\build.ps1 -Portable      # Self-contained build; no installed runtime needed
.\smoke-test.ps1           # Packaged app, launchers, renaming, and clean shutdown
```

Build output goes to `dist`. Tests cover native window movement, repeated swaps,
account identity, client restarts, overlapping non-game windows, contextual hotkeys,
profile changes, settings validation, and restoration of styles and positions.
They do not attach to real WoW clients.

Exit the tray app before testing to exercise the foreground and synthetic pointer
groups. When it is already running, tests skip those groups and use simulated
pointer / focus input to avoid competing with the live session. Real WoW focus
behavior and rendering after resize require a live-session check.

See [implementation decisions and verification notes](docs/implementation.md)
for design rationale and known validation limits.

---

WoWWindowKit is an independent utility. The source and imported layouts are
independent of the original app installation; its files, launchers, and settings
are left untouched. No upstream WowOpenBox code is included.
