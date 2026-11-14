# Implementation decisions and pre-mortem

## Mouse focus via Windows active window tracking (v1.0.6), PM-2 reversed

Decision (user, 2026-11-14): reverse PM-2's "must not modify global Windows
tracking" and switch focus the way WowOpenBox did.

Why: the v1.0.4 log recorded 124 hover activations of 40 ms or more (SetForegroundWindow
plus input-queue attachment): median 182 ms, 90th percentile 423 ms, max 794 ms,
with vsync on and five clients. Until activation finishes, held keys go to the
previous window. Windows' active window tracking activates the window under the
pointer inside its own input processing, so input that follows the movement is
already routed to the new window, with no foreground-lock workaround.

Design:
- The low-level mouse hook (dedicated thread) evaluates each new pointer position
  before Windows processes it. Tracking is on only when that position is over an
  exposed managed window (WindowFromPoint + GA_ROOT + snapshot, as before) and
  mouse focus is enabled, not paused, not editing Settings, and no mouse button is
  held. Because tracking is switched off before Windows handles a move onto another
  app, tracking never activates a non-game window; PM-2's boundary still holds.
- Values while on: tracking on, delay 0 ms, no Z-order raise. Off restores the
  user's original three values (a user who already uses tracking keeps it).
- SystemParametersInfo is called without SPIF_UPDATEINIFILE: memory only, never
  written to the profile, so logoff or reboot always restores the user's setting.
- A marker file (window-tracking.restore) records the original values once tracking
  has been enabled; the next start re-applies them after a crash. Exit, ProcessExit
  and unhandled exceptions restore as well.
- The app's own activation remains as fallback: if the hovered client is still not
  foreground 150 ms after the pointer entered it with tracking on, the focus worker
  activates it and logs that Windows did not.
- Still no input injection and no click or key broadcasting.

Verification: 120 integration checks pass, covering the hook gate (on over managed
windows; off outside and during pause, Settings or held buttons; one system call per
transition), the grace period before fallback, tracking values and marker handling,
crash repair, and restore on exit. An end-to-end test with the real system setting
(Windows focuses a hovered mock client while the app's own activation is disabled)
runs only with the tray app closed.

## Configurable hotkeys (v1.0.5)

Swap keys were editable only as typed text and limited to F1–F12; the mouse-focus
toggle was fixed at Ctrl+Shift+F. Settings now records keys by pressing them
(swap-key column and a toggle-key box), and `MouseToggleHotkey` is a saved setting
(older settings files keep the Ctrl+Shift+F default).

Allowed keys: F1–F24, numpad, Insert/Delete/Home/End/Page Up/Page Down, Pause and
Scroll Lock with any modifiers. Letters, digits, punctuation, Space and arrows
require Ctrl or Alt, because the hook consumes bound keys while WoW is active and
plain typing or movement keys would break chat and movement. Lone modifiers and
the Windows keys are never bindings. Taking another slot's key swaps the two, so a
profile never becomes invalid mid-edit. Validation rejects a toggle key that equals
any profile's swap key. Hand-edited names are case-insensitive and are written back
in a canonical form.

The keyboard hook now sees every key, so its first step is one dictionary lookup in
the published snapshot; unbound keys return without further work.

Verification: 110 integration checks pass, adding parsing/round-trip, rejected
forms (plain letters, numeric names, modifier-only), toggle/swap conflict, custom
numpad, Alt+digit and multi-modifier swap and toggle routing (old keys pass through), and recording through the
grid's real key path including the swap-on-conflict rule and navigation passthrough.

## Input latency under hover focus (v1.0.4)

Observed by user: holding a key while sweeping across clients, the key often
arrives late or needs a second press; one client (account 6) suffers most.
WowOpenBox did not show this.

Cause [evidence: TrayApp, KeyboardHook, MouseHook, Native.Activate]: both
low-level hooks were installed on the UI thread. Windows holds every keystroke
and mouse event system-wide until a low-level hook callback returns, and that
thread also ran each hover activation synchronously (including the fallback
that attaches to game input queues and calls SetActiveWindow/BringWindowToTop,
which wait for the game's own thread), the one-second window scan, and UI
refreshes. Every slow focus change therefore delayed the very key the user was
holding.

Changes:
- Hooks run on a dedicated high-priority thread with its own message loop. The
  callbacks read an immutable snapshot (managed HWND/PID, swap keys, pause,
  editing, mouse focus) published by the UI thread, and only post work or wake
  the focus worker.
- Hover focus runs on a separate worker thread, woken by mouse events, with the
  configured poll interval as fallback. Activation is serialized with UI-thread
  swaps by a lock.
- Activation first attaches only to the current foreground thread, which grants
  foreground rights without touching the target's input state or sending it
  synchronous messages. The previous full attach remains the last resort. No
  input is injected (PM-2 unchanged).
- Window scans no longer call Process.GetProcessById(...).ProcessName (a full
  system process snapshot per window process). Eligibility is cached by PID and
  creation time, which also guards against PID reuse.
- Activations taking 40 ms or more are logged to app.log with the window title,
  so slow clients can be identified from real sessions.

Verification: 92 integration checks pass, including key routing from a non-UI
thread, pause/forget reaching the hook snapshot immediately, and the focus
worker deciding without UI pumping. Rescan of the mock set averages under 1 ms.
The hook-thread input test and native foreground tests skip while the user's
app is running and need a run with the tray app closed. Live WoW acceptance:
hold a key while sweeping across all clients; check app.log for slow-focus lines.

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
