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

## Multiboxing policy and input broadcasting

**No input broadcasting. No gameplay automation.** WoWWindowKit arranges game
windows and changes which window has focus. It does not copy keystrokes or mouse
clicks to multiple clients, send gameplay commands to background clients, or
perform rotations, movement, or other gameplay actions for you. You control each
account yourself.

[Blizzard's multiboxing policy](https://eu.support.blizzard.com/en/article/24258)
states that playing multiple WoW accounts is not itself a violation of its EULA.
However, its
[official input-broadcasting policy](https://us.forums.blizzard.com/en/wow/t/policy-update-for-input-broadcasting-may-2021/956613)
also prohibits software or hardware that mirrors commands across accounts or
automates or streamlines multiboxing. **The absence of broadcasting alone does
not establish compliance.** Blizzard has not approved WoWWindowKit; this README
cannot guarantee that its window-switching or hover-focus features are permitted
under Blizzard's interpretation of that policy.

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
broadcast input, or automate gameplay. For mouse focus it switches on Windows'
active window tracking only while the pointer is over a managed WoW window, never
saves that change to your profile, and restores your own setting everywhere else.

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

**Install a shortcut:** double-click **Install shortcut.cmd** in the repository or
the built `dist` folder. This adds **WoWWindowKit** to your Start menu and desktop
with the project icon. Find it in Start, right-click it and choose **Pin to taskbar**.
You can also right-click the desktop shortcut (under **Show more options** if needed).
The shortcut opens controls with your last selected profile; it also reopens controls
when the app is already in the tray. Run the installer again after moving the folder.
Use `Install shortcut.cmd -NoDesktop` for Start only, or `Install shortcut.cmd -Remove`
to remove both shortcuts. The app stays in its existing folder; no administrator access
is needed. Pinning is the manual final step.

1. **Close other window-management tools.** Tools that also control window
   layouts or mouse focus can compete with WoWWindowKit.
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

Swaps run one transition at a time. During a burst of hotkeys, the latest account
selection replaces any waiting selection. The next transition starts after all
affected windows have stayed at their target rectangles for 50 ms. The first
selection starts immediately; queued selections may wait briefly for the game.
Pause, Settings and layout/assignment changes discard waiting selections. A
waiting hotkey is also discarded if you switch away from the game.

If a transition has not settled after two seconds, the app drops the waiting
selection and blocks further swaps until the pending moves settle. It does not
retry the resize automatically. `app.log` records numbered swap requests, queue
replacement, requested/observed bounds, completion times and timeouts. These
measure window geometry; they do not guarantee the graphics device is healthy.

Edit coordinates and borderless mode in **Settings & layout**, or manually move
and resize your clients and save their current positions from the tray menu.
Switching to 5-man restores extra clients and excludes them from focus and
hotkeys. Switching back reuses their account numbers.

### Mouse focus

Hover focus works only over an exposed, managed WoW window. Move over the desktop,
another app, or a window covering WoW and focus changes stop. Return to a managed
game window and they resume.

Focus switching uses Windows' own *active window tracking*:
Windows changes focus while it handles the mouse movement, so a key you hold while
sweeping across clients reaches each client as soon as the pointer enters it.
The app turns tracking on only while the pointer is over an exposed managed WoW
window (no delay, windows are not raised) and restores your own setting
elsewhere. The change is memory-only: exiting, logging off or rebooting restores
it. At startup the app records your original values in `window-tracking.restore`
beside the executable, so if it is killed while tracking is on, the next start
repairs it. If that file cannot be written, or Windows refuses a tracking change,
the app leaves tracking off for the rest of the run, shows a tray warning, and
switches focus itself instead. If Windows has not switched to the hovered client
within 150 ms, the app switches focus itself.

Focus pauses while a mouse button is held or Settings is open. After a hotkey
swap, hover focus waits until the clients have finished moving to their new slots,
so a small mouse movement during the swap cannot hand focus back to the previous
main. That wait is capped at 1 second, so a client that never applies its resize
cannot block hover focus; a client still moving after 1 second can receive hover
focus again. A stationary pointer then leaves the chosen account active; the first mouse
movement resumes hover focus. Mouse transitions are event-driven, with a periodic
fallback for windows appearing beneath a stationary pointer.

Automatic layout enforcement has been removed: repeatedly correcting a client's
frame and size caused unstable resizing during rapid swaps. The app now records
layout drift in `app.log` without correcting it. Use **Reapply layout / reset main**
for an explicit layout reset. The original game-driven resize issue remains under
investigation; a live incident showed DX12 graphics-device recovery immediately
before the wrong size returned. If Reapply immediately reverts again, do not keep
repeating it. To arrange windows by hand for **Save current window positions**,
pause first.

## Everyday controls

The compact dark control window opens on startup. It shows each slot, its hotkey,
the assigned client's PID and title, and the current main account. Management
continues while it is visible. The tray icon's right-click menu provides the
same core actions.

| Action | Where / how |
| --- | --- |
| Bring an account to main | F1–F5 / F1–F10 while a managed WoW window is active, or **Make main** in controls |
| Toggle mouse focus | Ctrl+Shift+F by default (changeable in Settings) while WoW is active, or the controls / tray toggle |
| Assign a running client | Select a slot and window, then **Catch / assign** |
| Release a client | **Forget** restores its original borders and position |
| Recover one stuck client | **Restart client** closes it; start WoW again and log in, and the new client takes its place |
| Change profile, pause, or reapply layout | Controls or tray menu |
| Edit layout, hotkeys, or app name | **Settings & layout**, or `Settings.cmd` |
| Reopen controls | Double-click the tray icon, **Show controls**, or `Controls.cmd` |
| Restore windows and quit | **Exit** in controls or the tray menu |

Closing the control window or choosing **Hide to tray** keeps the app running.
Use `WoWWindowKit.exe --tray` on first launch to start hidden. Only one instance
runs per Windows session, and later launches are handed to it:

| Command line | First launch | App already running |
| --- | --- | --- |
| *(none)* or `--controls` | Start and open controls | Open controls |
| `--tray` | Start hidden in the tray | Nothing else to do |
| `--settings` | Start and open Settings | Open Settings |
| `--exit` | Do nothing | Restore windows and quit |
| `--profile <name>` | Start with that profile | Switch to that profile, then do the above |

`--profile` accepts any profile name in `settings.json`, ignoring case, so a
launcher for a custom profile works too. An unknown name stops a first launch with
an error; a running instance keeps its profile and shows a tray warning.

### Changing hotkeys

In **Settings & layout**, select a cell in the *Account swap key* column and press
the new key combination. To change the mouse-focus toggle, click the *Mouse focus
toggle key* box and press the new combination. Nothing is typed; the key you press
is recorded. Changes apply when you click **Save & apply**.

- Usable on their own, with any of Ctrl, Shift and Alt: F1–F24, numpad keys,
  Insert, Delete, Home, End, Page Up, Page Down, Pause and Scroll Lock.
- Letters, digits, punctuation, Space and arrows need Ctrl or Alt (for example
  **Ctrl+1** or **Alt+Q**), so chat typing and movement keep working.
- Pressing a key that another slot already uses swaps the two slots' keys. A
  swap key and the toggle key can't be the same.
- Tab, Enter, Escape and the plain arrow keys keep their usual meaning in the dialog.

Each profile (5-man, 10-man) has its own swap keys. A bound key is consumed only
while a managed WoW window is active; elsewhere it passes through unchanged.


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

| JSON key (Settings label) | Type, default | Behavior and validation |
| --- | --- | --- |
| `DisplayName` (**App name**) | text, `Notepad++` | Control / Settings titles, tray label and application dialogs; game titles are unaffected. 1–80 characters. |
| `ActiveProfile` (**Layout**) | text, `5-man` | The profile in use; must name one of `Profiles`. |
| `MouseFocus` (**Focus follows mouse over WoW**) | true/false, `true` | Hover focus on or off; also the toggle key and the controls / tray toggle. |
| `Borderless` (**Borderless windows**) | true/false, `true` | Removes caption and frame from managed clients while they are in a slot. |
| `MousePollMs` | number, `50` | Fallback mouse-focus polling interval in milliseconds; 20–1000. |
| `MouseToggleHotkey` (**Mouse focus toggle key**) | text, `Ctrl+Shift+F` | Same rules as swap keys (see *Changing hotkeys*); must differ from every swap key. |
| `ProcessNames` | list of text, `Wow`, `WowClassic`, `WowClassicT` | Exact allowed executable names, without `.exe`; at least one. |
| `Profiles` | list | Each has a `Name` (unique, ignoring case) and 1–10 `Slots`. |
| `Profiles[].Slots[]` | `X`, `Y`, `Width`, `Height`, `Hotkey` | Slot 1 is the main window. Width and height 100–32768 pixels, X and Y within ±100000; each slot's `Hotkey` is unique in its profile. |

You can rename `WoWWindowKit.exe` while it is closed; update the four generated
`.cmd` launchers to use the new filename. Settings are located relative to the
executable regardless of its name. Windows Task Manager may show the executable
name or embedded product metadata separately from the configured app name.

### Restart client

After a DX12 device loss a client can stay stuck at a size and frame it chooses
itself, and each later resize fails again. Only a new game process clears it.
The controls show such a client as *frame restored by game, suspected graphics
reset* once two observations a second apart have seen the game's frame back.
That is a hint, not proof.

**Restart client** (in controls, for the selected slot) asks for confirmation,
then closes only that client, as Alt+F4 would. WoWWindowKit never starts WoW
itself, so start WoW again the way you normally do and log in. The next WoW client
that opens takes the restarted account's number and slot, main or not; no other
client is moved or restyled. While a restart is pending, the same button reads
**Cancel restart**, and layout, profile, assignment and Settings changes wait.

- If the client has not closed after 20 seconds, the app asks whether to end that
  process. **Keep it** cancels the restart and leaves the client in its slot.
- The reservation lasts 10 minutes. After that a client that is still running
  keeps its slot; otherwise the slot stays empty for normal discovery or
  **Catch / assign**.
- Only one restart runs at a time. The app cannot tell which account a new client
  logs into, so start one client at a time while a restart is pending.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| **Windows refuses focus changes** | Click a WoW window once and retry. Run WoWWindowKit at the same privilege level as WoW; it does not request elevation automatically. |
| **A client has the wrong number** | Use **Catch / assign** to select the intended account slot. |
| **A client is not discovered** | Check `ProcessNames` and whether the client was forgotten during this session. |
| **The app seems to have disappeared** | Check Windows' hidden tray icons, or run `Controls.cmd`. Closing controls hides the panel. |
| **Settings fail to load** | The startup error says so and names `settings.json`. Inspect it and the previous save in `settings.json.bak`; check `app.log` beside the executable. |
| **Another startup error** (for example, hotkeys could not be enabled) | Not a settings problem; details are in `app.log`. Check that no other tool blocks low-level keyboard hooks, and restart the app. |
| **Tray warning: active window tracking is off** | The app could not write `window-tracking.restore` or change the Windows setting. Hover focus still works through the app's own activation; keep the application folder writable and restart to restore the faster path. |
| **One client stays large, will not swap, or shows its frame again** | Its graphics device was probably reset (WoW's `Logs/gx.log` shows `Device context was lost`); Reapply layout does not fix it. Select its slot and click **Restart client** (see below). |
| **Windows remain borderless after a crash** | Relaunch the app and exit normally, or restart the game clients. |

Mouse focus uses Windows' active window tracking. Keyboard swaps and the hover
fallback use foreground activation with a temporary input-thread attachment, run on
a background worker so a busy game cannot freeze the controls or tray. Threads are
detached immediately afterward, and hung windows are skipped.
Actual focus behavior and rendering after a resize depend on the game and the
Windows session.

## Build and test

Requires **Windows x64** and the **.NET 10 SDK**. There are no third-party packages.

```powershell
.\test.ps1                 # Integration checks using ten separate mock clients
.\test.ps1 -Groups settings,tracking-marker   # A subset; no windows, cursor or focus changes
.\build.ps1                # Smaller build; requires .NET 10 Windows Desktop runtime
.\build.ps1 -Portable      # Self-contained build; no installed runtime needed
.\smoke-test.ps1           # Packaged app, launchers, renaming, and clean shutdown
```

Build output goes to `dist`. Tests cover native window movement, repeated swaps,
account identity, client restarts, overlapping non-game windows, contextual hotkeys,
profile changes, settings validation, and restoration of styles and positions.
They do not attach to real WoW clients.

Self-test groups, in run order: `settings`, `tracking-marker`, `swaps`, `hotkeys`,
`hover`, `activation`, `assignment`, `controls-ui`, `hooks` and `live-tracking`.
The first two need no windows. Each other group starts the same three mock clients
first, so it can also run alone; in a full run, later groups reuse what earlier
ones left behind (more clients, the 10-man profile, edited settings).

Exit the tray app before testing to exercise the foreground and synthetic pointer
groups. When it is already running, tests skip those groups and use simulated
pointer / focus input to avoid competing with the live session. Real WoW focus
behavior and rendering after resize require a live-session check.

See [implementation decisions and verification notes](docs/implementation.md)
for design rationale and known validation limits.

