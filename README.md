# WoWWindowKit

A small Windows tray app for David's WoW window layouts, account swapping, and
focus following the mouse. The display name defaults to **Notepad++** and can be
changed in Settings at any time.
The interface is dark only, including layout controls, menus, and app dialogs.

## Use

1. Exit the original OpenMultiBoxing app first, so both apps do not control the
   same windows or keys. Its normal exit also disables its Windows-wide mouse
   tracking. This app does not change that Windows setting.
2. Run **Start 5-man.cmd** or **Start 10-man.cmd** from `dist`.
3. Start your WoW clients normally. They attach automatically, in process-start
   order; existing `WoW 1` … `WoW 10` titles retain those numbers when possible.
4. Press **F1–F5** or **F1–F10** while a managed WoW window is active to move that
   account into the large slot and activate it. Each account keeps its number.

Swapping matches your old setup: choosing account N puts N in the main slot,
account 1 in slot N, and returns the previous main account to its own slot.
Ctrl+F1 etc. have no special behavior. Hotkeys pass through normally in other apps.

Move the mouse over a managed WoW window to focus it. Move over another app,
the desktop, or an app covering WoW and this app stops changing focus. It resumes
when you return to an exposed managed game window. There is no mouse movement,
click injection, input broadcasting, or gameplay automation. Focus pauses while
holding a mouse button or modifier, during Settings editing, and for 350 ms after
a swap so the mouse does not immediately undo the keyboard selection.

Right-click the tray icon (possibly under Windows' hidden-icons arrow) to:

- Switch between **5-man** and **10-man**.
- Toggle mouse focus; **Ctrl+Shift+F** also toggles it while WoW is active.
- Pause the app, or reapply the layout with account 1 as main.
- Save current window positions after manually moving/resizing game windows.
- Open **Settings & layout** to change the name, hotkeys, coordinates, borderless
  mode, and which game window belongs to each account.
- Exit and restore the original window styles and positions.

Double-click the tray icon or run **Settings.cmd** to open Settings. Layouts use
physical desktop pixels and retain your original coordinates:

| Profile | Main | Other windows |
| --- | --- | --- |
| 5-man | 1880 × 1392 at 0,0 | Four down the right at x=1880 |
| 10-man | 2048 × 1152 at 0,0 | Four down the right, five along the bottom |

Closing a client leaves its account number empty. A new client fills an empty
number; use the assignment controls if launch order is ambiguous. Assignments
are retained for the lifetime of the app; they are not saved as account identities
between sessions. Switching to 5-man restores extra clients and excludes them
from focus/hotkeys. Switching back reuses their account numbers.

## Name and configuration

**App name** changes the Settings window title, tray label, and application dialog
titles immediately on save. It does not change game titles. You can also rename
`WoWWindowKit.exe` while it is closed; update the three `.cmd` files to the new
filename. The app locates settings relative to its executable, independent of its
filename. Windows Task Manager may display the executable or embedded product
metadata separately from the chosen window title. No separate rename script is needed.

`settings.json` is created next to the executable on first launch from
`settings.default.json`. Saves are validated, atomic, and preserve the previous
file as `settings.json.bak`. Advanced settings include `MousePollMs` (default 50)
and the exact allowed executable names without `.exe`. Only WoW, WowClassic, and
WowClassicT qualify by default. Changes made manually require restarting the app.
Keep this folder writable.

One instance runs per Windows session. Starting another profile shortcut tells
the running app to change profile. Starting Settings opens its existing editor.

Windows can refuse foreground activation. If the tray reports that, click a WoW
window once and retry. Run at the same privilege level as WoW. No elevation is
requested automatically. Activation first uses the normal foreground API, then
temporarily shares input focus with the foreground/target UI threads if needed;
the threads are detached immediately afterward, and hung windows are skipped.
On a crash, the global Windows mouse setting remains
unchanged; borderless game windows may need restoring by relaunching this app
and exiting normally, or by restarting the clients.

## Build and test

Requires the .NET 10 SDK on Windows; there are no third-party packages.

```powershell
.\test.ps1
.\build.ps1                 # Small build; .NET 10 Windows Desktop runtime required
.\build.ps1 -Portable       # Self-contained build; no installed .NET required
.\smoke-test.ps1           # Packaged app, shortcuts, renaming, and clean shutdown
```

Tests launch and close ten separate mock client processes. They verify native
window movement, repeated swaps, account identity, client restart, overlapping
non-game windows, contextual hotkey dispatch, profile changes, settings validation,
and style/placement restoration. They do not attach to real WoW processes.
Real WoW focus behavior and game rendering after resize need a live-session check.

Source and imported layouts are independent of the original app installation.
The original app, batch launchers, and settings are not modified. No upstream
WowOpenBox code is included.
