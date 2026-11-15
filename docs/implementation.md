# Implementation decisions and pre-mortem

## Serialize swaps and measure settling (v1.0.9)

Failed future: a delayed client receives another burst of asynchronous resizes,
or a queued hotkey fires after Pause/settings/reassignment and changes the wrong
account. Success with prompt mock windows conceals another DX12 regression.

- PM-7 — High — Overlapping asynchronous transitions. [evidence: Swap replaces
  settling on every call; ApplyLayout issues asynchronous positions immediately]
  Permit one active transition. Observe every affected window at its target for
  a short quiet interval before issuing the latest queued request. Do not count
  the hover-focus timeout as successful resize completion. On swap timeout drop
  pending work and retain a fence until the old moves settle; never auto-retry.
- PM-8 — High — Deferred selection changes semantics or survives cancellation.
  [evidence: stable Accounts mapping, Paused/Editing, ApplyLayout, Assign/Forget]
  Store account plus window identity; validate again before execution. Preserve
  SlotFor mapping and focus only when the corresponding transition actually
  starts. Cancel queued selections on pause, editing, settings and explicit layout
  changes. Input hooks remain snapshot-only and never wait for a game.
- PM-9 — Medium — Diagnostics or tests misrepresent graphics completion.
  [evidence: gx.log DeviceRecovery; native bounds do not expose GPU completion]
  Log request IDs, queue replacement, issued moves, observed bounds, elapsed time,
  cancellation and timeout. Bound logging to events/changed observations. Verify
  delayed/failing mock positioning, cancellation and latest-wins behavior. Native
  geometry settling is a heuristic, not proof of DX12 device health. Real-client
  acceptance and graphics-log correlation remain required.

Guardrails: keep styles out of swaps; no automatic drift correction; preserve
stationary-pointer and outside-game focus rules, restore-on-exit and settings.
Build/test a separate candidate while the user's v1.0.8 session is running.
Verdict: PROCEED WITH GUARDRAILS. Do not restart live clients to run tests.

Implementation: a UI-owned active transition and single replaceable selection
are serviced by a 25 ms UI timer. Every moved window must report its expected
rectangle continuously across observations for at least 50 ms. The independent
one-second hover timeout never releases the scheduler; a two-second swap timeout
drops the waiting selection and fences new swaps until those moves settle.
Late completion cannot replay discarded requests. Foreground validation applies
to hotkey requests; explicit Make main actions retain their existing behavior.
No new native style changes, drift corrections, input injection or dependencies.

PM-7 resolution: Mitigated. One active batch, partial/jittering geometry tests,
latest-selection replacement, independent hover timeout and no-retry swap fence.
Geometry is a heuristic and cannot prove the GPU has released rendering resources.
PM-8 resolution: Prevented for the tested cancellation/identity paths. Covers
pause, editing, settings, reset, profile, reassignment, leaving the game, losing a
managed window, and revalidating the deferred target. Placement failure clears
the scheduler and preserves the logical main using the existing rollback path.
PM-9 resolution: Mitigated. Request/move/observation/settlement/timeout logs include
IDs and elapsed timings; tests inject delayed positioning and controlled time.

Verification: Release and packaged-candidate integration runs each passed 174
checks. Release build has zero warnings/errors. Four foreground/pointer groups
were skipped because the user's live app was running; packaged launch/IPC smoke
checks also remain outstanding because their singleton would target that session.
The first live-session test attempt exposed a mock hit-test fixture depending on
the foreground game's Z order; the fixture now exposes only its own mock main
without activation while the real app runs. No real game client was targeted.

The packaged candidate is artifacts/swap-candidate-1.0.9. Its executable was
SHA-256 verified and staged in dist for the next launch; the loaded v1.0.8 image
was renamed to dist/WoWWindowKit.previous-1.0.8-20261115-012830.exe and verified.
User settings were hash-verified unchanged. PID 38868 and the five WoW clients
were left running. Fresh-session DX12 acceptance is still outstanding; this is
not evidence that the descriptor-allocation failure is fixed.

## Live recurrence: graphics-device recovery (2026-11-15)

v1.0.8 remained running (PID 38868) with the same five WoW processes after the
initial successful test. At approximately 01:05 the user switched from F2 back
to F1 and reported account 1 at the wrong size. The screenshot and live native
queries agree: PID 13460 at (0,0), outer size 1827x1353, client size 1813x1339,
style 0x14050000, extended style 0x00040100, DPI 96, not hung or maximized.
All four followers retained their exact configured rectangles, style 0x14000000
and extended style 0x00040000. WoW's Config.wtf already contained
GxWindowedResolution "1827x1353", last written before this incident.

The shared WoW Logs/gx.log supplies previously missing evidence:

- 01:05:29.645: `OutOfMemory: AllocDescriptors Failed (Prism:(0))` and
  `OutOfMemory: RTV view create failed (Prism:(0))`.
- 01:05:29.716: `Device context was lost. Attempting recovery. Occurrence: 1`.
- 01:05:29.720: `Doing GxRestart. Reason:DeviceRecovery`, then `Dx12 Device Destroy`.
- 01:05:31.808: `Dx12 Device Create Successful`.
- 01:05:32.230: WindowKit's passive observer reports account 1 at 1827x1353,
  expected 1880x1392, with frame bits restored. It performs no correction.

Inference: graphics-device recreation restores the game's saved window size and
frame, explaining why repeated external frame/size enforcement was a poor repair.
The shared graphics log does not label the affected process. Timing supports this
connection but does not prove the source of descriptor exhaustion or whether
WindowKit's repeated swaps contribute. The report shows 2786 MB used against a
23370 MB GPU budget; the error must not be summarized as proof that the GPU's
physical memory is full. Descriptor allocation and total VRAM are different limits.

Evidence preserved in artifacts/live-window-incident-20261115{.json,.log,-gx.log}
and the user's screenshot preview at artifacts/w1-preview.png. No client, game
setting or running app was changed during capture. Existing explicit Reapply
layout remains the immediate recovery action; do not reinstate the periodic
correction loop. Further verification must cover a real graphics-device reset,
not just mocked window movement. This recurrence leaves PM-6 inconclusive for
long-running use and confirms that v1.0.8 contained only the enforcement regression.

Follow-up at 01:10: user tried explicit Reapply layout, saw the correct size
briefly, then the faulty size returned immediately. The graphics log again
records descriptor allocation / RTV creation failure at 01:10:36.382, device
loss occurrence 2 at 01:10:36.391, and device creation success at 01:10:37.988.
WindowKit reports the same 1827x1353/frame drift at 01:10:38.425. Thus Reapply
layout is not a working recovery for this session; do not recommend repeating
it. This recurrence is more specific than a stale geometry/style setting:
external reapplication coincides with another graphics-device recovery.
Snapshots: artifacts/live-window-incident-20261115-reapply{.log,-gx.log}.

## Contain the automatic-layout regression (v1.0.8)

User incident, 2026-11-14: after logging in all clients and quickly selecting
F2, F3, F4, F5, windows repeatedly resized and moved. Commit c098696 added a
250 ms enforcement loop with synchronous positioning and frame-style changes.
The local dist/app.log at 23:11:30 records account 3 already at its exact target
rectangle with style 0x14050000; enforcement still rewrote its frame. Subsequent
entries show accounts 3 and 5 alternating between the intended rectangles and
1827x1353 / 1841x1367 rectangles. Focus calls took more than six seconds.
The loop participated in the incident; the game's reason for rejecting sizes
and the precise causal ordering cannot be established from this log alone.

Failed future: another automatic repair fights WoW's delayed window changes
and makes rapid swaps unstable again. Removing enforcement contains that
regression but leaves the original game-driven resize issue unresolved.

- PM-5 — High — Polling fights the game. [evidence: c098696 EnforceLayout;
  incident log above] Replace enforcement with observation only. Idle polling
  must never position, restore, activate, or change the style of an existing
  client. Detect with native position/style message counters after repeated
  simulated game changes, including style-only drift at correct bounds.
  Resolution: Prevented by removing all correction writes from the observer.
- PM-6 — Medium — Mock success is mistaken for real-game acceptance.
  [evidence: SelfTest's mock windows apply resizing promptly; incident above]
  Preserve swap/account/focus tests, test repeated observation between rapid
  swaps, and explicitly retain real-client acceptance as outstanding. Never
  claim the original drift problem is solved by this containment build.
  Resolution: Mitigated by focused regression checks and explicit limits.

Guardrails: retain explicit layout/reapply and swap behavior; restore the
one-second discovery timer; deduplicate passive drift logs. Preserve user
settings and the failed binary/log before replacing the next-launch binary.
Verdict: PROCEED WITH GUARDRAILS. Live WoW rapid F2–F5 acceptance remains required.

Verification: Release build has zero warnings/errors. 156 integration checks
passed with zero skipped groups, including five attached clients receiving rapid
F2–F5 bindings, frame-only drift at correct bounds, repeated game-driven geometry
changes, maximization, zero native position/style messages from repeated polling,
and explicit Reapply layout. Nine packaged smoke checks passed. The first smoke
run hit a sharing violation while polling settings during a save; the retry passed.
Failed binary, settings and app.log were copied and SHA-256 verified under
artifacts/incident-c098696-20261114-231848 before rebuilding dist as v1.0.8.
No real WoW clients were running: PM-6's real-client acceptance remains outstanding.

## Swap keeps focus on the new main (v1.0.7)

Observed by user: pressing F5 to make the hunter main left focus on the old main,
which ended up in the hunter's small bottom-right slot.

Ruled out [probe on live clients]: WoW does not drag the cursor when its active
window is moved or resized, and the app never moves it. A mock-window swap with
real Windows tracking also kept focus correctly, so this is not Windows re-checking
a stationary pointer by itself.

Cause [inference]: swaps use asynchronous SetWindowPos, which the game applies on
its own thread a frame or more later. The old main has just lost focus and renders
at background frame rate, so for roughly 100–300 ms (focus timings in the log)
it is still the big window under the pointer. Any mouse movement in that window,
including sensor jitter, makes hover focus (Windows tracking since v1.0.6, app
activation before that) activate the window under the pointer: the old main.
It then shrinks into the new main's old slot with focus. Mock windows apply moves
immediately, which is why they could not reproduce it.

Fix: a swap records the target bounds of every window it moves. Until all of them
report those bounds (or 1 s passes), the mouse hook keeps Windows tracking off and
the focus worker makes no hover decisions. Tracking also stays held off after a
swap until the pointer actually moves. Tests cover jitter during the resize, resume
after settling, the timeout, and a live swap with real tracking.

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
