# Implementation decisions and pre-mortem

## Compact controls (v1.0.3)

Add a dark modeless control window, visible by default: live account list, profile,
mouse focus, pause, catch/assign, forget, make main, and access to layout settings.
Closing hides it; the existing ApplicationContext owns the lifetime. The panel's
visibility must not set Editing or pause game focus. Refresh labels from existing
manager notifications/scan ticks without adding a second discovery loop. Changes
use the same manager methods as the tray. Modal settings remain separate. Test
hide/reopen without detachment, catch/forget callbacks, profile switching, and
single-instance commands distinguishing Controls from Settings.

Verification: 90 integration checks passed with no skipped groups; 9 packaged
smoke checks cover default startup UI, hide/reopen via a second launch, profile
switching, separate modal settings, rename support, and clean shutdown. Native
screenshots verify the compact panel is dark throughout. Closing with external
WM_CLOSE exposed a different CloseReason than the title-bar X; both now hide the
panel consistently. The input-hook regression probe now uses test-only movement
input under a real message loop rather than relying on SetCursorPos notifications.

## Catch / forget follow-up (v1.0.2)

PM-4 — Medium — A forgotten window is immediately recaptured by the existing
one-second auto-discovery loop. [evidence: Rescan automatically fills free slots]
Keep a session exclusion set keyed by HWND, PID, and process start time. Forget
restores the original window and excludes it from layout, mouse focus and keys.
Only explicit catch clears the matching exclusion. Replacing an occupied slot
with an outside window also excludes the displaced client. Switching two managed
clients exchanges their account numbers. Reject stale/closed choices and invalid
slot numbers without mutating assignments. Freeze discovery/focus while choosing
from tray menus. Catch resets numbered positions so selecting slot N also gives
the window that account's swap key. Test repeated rescans, restoration, recapture,
replacement, closed candidates, both profile sizes, and dark tray menu wiring.
Verdict: PROCEED WITH GUARDRAILS. Original app folders remain untouched.

PM-4 resolution: Mitigated. 83 native-window/integration checks pass with no
skipped groups, including tray action wiring, forgotten-window restoration and
exclusion through repeated scans, explicit reassignment, slot 10 replacement,
displaced-window exclusion, and rejection of a closed menu candidate. Tests also
caught a startup visibility edge case: restoring a newly created client's placement
could leave it hidden. Restoration now explicitly shows an originally visible
window if needed. Release compilation is clean after the nullable test fix.

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

### Responsiveness follow-up

Observed by user: swaps and hover focus feel slower than WowOpenBox.
[evidence: ApplyLayout, Swap, MouseTick, TrayApp timer; upstream MoveAndResize]
The first implementation synchronously reset every client's frame on every swap,
polled for mouse transitions at 50 ms, and suppressed hover focus for 350 ms.
Upstream skips unchanged geometry and uses asynchronous SetWindowPos (0x6604).

PM-3 — Medium — Fast swaps race with pending moves, or mouse events undo hotkeys.
[inference] Asynchronous geometry can still reflect the previous layout, so using
GetWindowRect alone to decide which swap moves to skip can lose a rapid second
swap. Use the logical previous/new slot mapping to select affected accounts;
always enqueue their new positions, preserve per-client order, and test rapid
swaps without waiting between requests. Keep style changes out of the swap path.
Replace timed mouse suppression with a stationary-pointer guard cleared by the
first actual movement. Observe mouse events without swallowing them; coalesce UI
work so hooks remain short. Keep outside-game checks and the periodic fallback.
Verdict: PROCEED WITH GUARDRAILS. PM-1/PM-2 remain invariants; PM-3 requires targeted
native-window regression tests before deploying the rebuilt executable.

Resolution: Mitigated in v1.0.1. The published candidate passed 66 checks,
including native message counters confirming unchanged clients receive no swap
position/style changes, and rapid queued swaps reach the correct final layout.
The pointer guard is tested with deterministic input. Two test groups (native
foreground assertions and synthetic cursor movement) were explicitly skipped
because the user's tray app and five real clients were active; input hooks install
successfully. Full foreground/input checks remain available with the tray app
closed. The existing session was left running; v1.0.1 replaces the next-launch
executable and the running v1.0.0 image is retained as a backup.

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
