# Implementation decisions and pre-mortem

Scope: independent C# Windows tray app, 5-man/10-man layout profiles, borderless windows,
account-specific swap hotkeys, and focus following the mouse only over managed WoW windows.
No focus-only bindings. Original app folders, launchers, and settings remain untouched.
Profile coordinates and bindings were read from the two existing wowopenboxSettings.tcl files.
No upstream source is copied into the implementation.

The editable display name defaults to Notepad++ to match the existing rename
script. This controls app window/tray/dialog titles; executable renaming is
supported independently. Single-instance IPC uses a fixed hidden control window
name, independent of the display name and executable filename.

## Failed future

After several swaps, F3 selects the wrong account, or moving over a browser steals
focus back to a game behind it. Compilation alone would not expose these mistakes.

## PM-1 — Medium — Account identity and swap semantics

Basis: [evidence: original WowOpenBox.tkapp, SetAsMainInt]. An account's identity
is independent of its current position. Selecting N restores the previous main
account to its home slot, places account 1 in slot N, and puts N in slot 1.
Implement that rule directly, retain assignments across rescans, validate process
identity when handles are reused, and preserve empty slots when clients exit.
Existing numbered WoW titles are hints when first attaching; otherwise assign in
process-start order. Provide manual account assignment for ambiguous starts.
Detection: synthetic real-window integration tests and real-client acceptance.
Resolution: Mitigated; stable mapping and repeated-swap/restart tests.

## PM-2 — Medium — Focus escapes the game boundary

Basis: [evidence: original ShouldPause/PeriodicChecks/UpdateMouseFollow]. Original
uses system-wide active-window tracking and pauses outside managed windows.
Replacement must not modify global Windows tracking. Use WindowFromPoint plus
GA_ROOT and an exact managed-handle check; never infer visibility from rectangles.
Do not consume F-keys while another application is active. Pause mouse focus during
dragging/modifiers and briefly after a swap; provide a pause toggle. Check actual
foreground activation and report failure rather than injecting Alt/clicks.
Detection: overlapping non-game test windows and keyboard dispatch tests.
Resolution: Mitigated; only managed top-level windows qualify, contextual key hook.

## Guardrails / verdict

PROCEED WITH GUARDRAILS. PM-1 and PM-2 above are acceptance constraints. Preserve
original styles/positions for restore-on-exit; stop discovery/focus during settings
editing. Settings saves are validated, atomic, with a previous-version backup.
Build and test before publishing. Actual WoW focus behavior remains a user-session
acceptance check, since Windows can refuse foreground activation and no live
game clients were available during initial implementation.

## Primary API references

- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getancestor
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
- https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-overview-windows-forms

## Verification and risk calibration

- Release build: zero warnings and errors; no third-party packages.
- 60 integration checks passed against the published executable, using separate
  mock-client processes. Includes repeated swaps, ten-account layout, restart,
  assignment, real cross-process hover activation, overlap exclusion, keyboard
  routing, settings editing, and original style/placement restoration.
- Five packaged smoke checks passed: first-run settings, single-instance profile
  switching, IPC across filenames, renamed executable/custom title, and clean exit
  with the modal Settings editor open.
- Actual packaged Settings window captured and visually checked: dark client area,
  grid, controls, and DWM title bar. Dark mode is forced independently of Windows'
  preference; validation/error dialogs and the tray menu also use the dark palette.
- PM-1: Mitigated; real HWND geometry and stable identities verified across swaps,
  account exits/restarts, and manual reassignment.
- PM-2: Confirmed during development, then mitigated. Plain SetForegroundWindow
  was intermittently refused. The fallback temporarily attaches both foreground
  and target UI input queues, raises/activates the target, and detaches in finally.
  Focus is retried with a short throttle, with a visible status if Windows refuses.
  Native focus-resume and client-to-client tests pass with that fallback.
- Packaging checks also found a quick-launch startup race and an initially hidden
  Settings dialog when invoked by a hidden launcher. Both were fixed and verified.
- Remaining acceptance: real WoW rendering after resize and live input/focus
  behavior. No real WoW clients were running for this verification.
