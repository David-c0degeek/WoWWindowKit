using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WoWWindowKit;

internal sealed class ManagedWindow
{
    internal required nint Handle;
    internal required int Pid;
    internal required long StartTicks;
    internal required string Title;
    internal required nint OriginalStyle;
    internal required Native.Placement OriginalPlacement;
    internal bool Valid => Native.IsWindow(Handle) && Native.GetWindowThreadProcessId(Handle, out var pid) != 0 && pid == Pid;
}

/// <summary>Immutable state read by the input-hook and focus threads without touching UI-owned collections.</summary>
internal sealed record InputState(IReadOnlyDictionary<nint, int> Managed, IReadOnlyDictionary<nint, int> Numbers, IReadOnlyDictionary<KeyBinding, (int Number, nint Handle)> SwapKeys, KeyBinding Toggle, bool Editing, bool Paused, bool MouseFocus);

/// <summary>
/// Owns client discovery, layout, swaps and hover focus. Threads (see docs/implementation.md,
/// "Current architecture"): the UI thread owns every mutable collection and the swap scheduler;
/// the hook thread calls <see cref="PointerMoved"/>; the focus worker calls <see cref="MouseTick"/>
/// and <see cref="ActivateQueued"/>. Off-UI code reads only the published <see cref="InputState"/>
/// snapshot, volatile fields, and state guarded by <c>trackingLock</c> or <c>focusLock</c>.
/// </summary>
internal sealed class WindowManager : IDisposable
{
    // ---- UI thread only ----
    private AppSettings settings;
    private bool paused, editing;
    private string status = "Waiting for WoW windows";
    private readonly Dictionary<int, (long Created, bool Eligible)> processCache = [];
    private readonly HashSet<(nint Handle, int Pid, long Start)> forgotten = [];
    private readonly Func<int, bool>? testProcess;
    /// <summary>Moves issued by the swap currently being started (null outside StartSwap).</summary>
    private List<(nint Handle, Rectangle Bounds)>? recordedMoves;
    private readonly Dictionary<nint, (Rectangle Bounds, long Frame, bool Zoomed, int Slot)> observedDrift = [];
    internal AppSettings Settings { get => settings; set { CancelSwaps("settings changed"); settings = value; processCache.Clear(); Publish(); } }
    internal Dictionary<int, ManagedWindow> Accounts { get; } = [];
    internal int MainAccount { get; private set; } = 1;
    internal bool Paused { get => paused; set { if (value) CancelQueuedSwap("paused"); paused = value; Publish(); } }
    internal bool Editing { get => editing; set { if (value) CancelQueuedSwap("editing"); editing = value; Publish(); } }
    internal string Status => focusRefused ? "Windows refused focus — click a WoW window once, then retry" : status;
    internal Action? Changed;

    // ---- Swap scheduler: UI thread only (25 ms timer). Hover threads read only swapWaiting/settling. ----
    private sealed record SwapRequest(long Id, int Account, ManagedWindow Window, long RequestedAt, bool RequireGameForeground);
    private sealed class SwapTransition(SwapRequest request, long startedAt, (nint Handle, Rectangle Bounds)[] targets)
    {
        internal readonly SwapRequest Request = request;
        internal readonly long StartedAt = startedAt;
        internal readonly (nint Handle, Rectangle Bounds)[] Targets = targets;
        internal readonly Dictionary<nint, Rectangle?> Observed = [];
        internal long? AtTargetSince;
        internal bool TimedOut;
    }
    private SwapTransition? activeSwap;
    private SwapRequest? queuedSwap;
    private long swapSequence;
    private long issuingSwapId;
    internal int SwapQuietMs = 50;
    internal int SwapTimeoutMs = 2000;

    // ---- Published for the hook and focus threads (written on the UI thread) ----
    private volatile InputState input = new(new Dictionary<nint, int>(), new Dictionary<nint, int>(), new Dictionary<KeyBinding, (int, nint)>(), default, false, false, false);
    internal InputState Input => input;
    /// <summary>After a swap, windows still on their way to new slots; null targets = not yet issued.</summary>
    private sealed record Settling(long Deadline, (nint Handle, Rectangle Bounds)[]? Targets);
    /// <summary>Written by the UI thread; cleared by any thread with a compare-exchange once settled.</summary>
    private volatile Settling? settling;
    private volatile bool swapWaiting;
    /// <summary>Upper bound for suppressing hover focus; the swap scheduler has a separate, longer timeout.</summary>
    internal int SettleTimeoutMs = 1000;

    // ---- Windows tracking gate: guarded by trackingLock (hook thread; UI thread in HoldTracking) ----
    // Holders keep the lock only for these fields plus one SystemParametersInfo call: no window
    // reads, rectangle reads or I/O, so the hook never waits behind slow work.
    private readonly object trackingLock = new();
    private Native.Point? trackingHeldAt;
    /// <summary>Written under trackingLock; read without it by the focus worker (grace period).</summary>
    private volatile bool trackingOn;

    // ---- Hover decisions: guarded by focusLock (focus worker; UI thread in StartSwap) ----
    private readonly object focusLock = new();
    private long hoverSince;
    private Native.Point? mouseAtSwap;
    private nint lastHover;
    private long retryFocusAfter;
    private volatile bool focusRefused;
    /// <summary>Last hover decision, for tests and diagnostics (focus worker).</summary>
    internal string MouseDecision { get; private set; } = "Idle";
    /// <summary>Raised on the thread that activated a window; owners must marshal UI work.</summary>
    internal Action? FocusWarningChanged;

    // ---- Seams: set once by the owner or by tests ----
    internal Func<Native.Point?> ReadPointer = static () => Native.GetCursorPos(out var point) ? point : null;
    internal Func<bool> MouseInputHeld = static () => Native.Down(Keys.LButton) || Native.Down(Keys.RButton) || Native.Down(Keys.MButton) || Native.Down(Keys.XButton1) || Native.Down(Keys.XButton2);
    /// <summary>Performs one activation; production calls it only on the focus worker.</summary>
    internal Func<nint, bool> ActivateWindow = Native.Activate;
    /// <summary>
    /// Queues a UI-thread activation job (keyboard swap, Make main) on the focus worker so a busy
    /// game cannot stall the UI thread. Null activates synchronously (tests without a worker).
    /// </summary>
    internal Action<Action>? RequestActivation;
    /// <summary>Bumped whenever swaps are cancelled (explicit layout, settings, profile, failure, exit); stale queued activations are dropped.</summary>
    private long activationGeneration;
    internal long ActivationGeneration => Interlocked.Read(ref activationGeneration);
    /// <summary>Turns Windows' active window tracking on or off and reports success; null leaves the system untouched.</summary>
    internal Func<bool, bool>? SystemTracking;
    /// <summary>How long Windows gets to activate a hovered window before the app does it itself.</summary>
    internal int TrackingGraceMs = 150;
    internal Func<nint, Rectangle?> ReadBounds = static handle => Native.GetWindowRect(handle, out var rect) ? rect.Rectangle : null;
    internal Func<long> SwapClock = static () => Environment.TickCount64;
    internal Func<nint> ReadForeground = Native.GetForegroundWindow;
    internal Action<nint, Rectangle> PositionSwapWindow = static (h, b) => Native.Position(h, b, frameChanged: false, asynchronous: true);
    internal Action<string> TraceSwap = Program.LogLine;

    private static (nint Handle, int Pid, long Start) Identity(ManagedWindow window) => (window.Handle, window.Pid, window.StartTicks);
    internal bool IsForgotten(ManagedWindow window) => forgotten.Contains(Identity(window));
    internal WindowManager(AppSettings settings, Func<int, bool>? testProcess = null) { this.settings = settings; this.testProcess = testProcess; Publish(); }

    /// <summary>Republishes the snapshot used off the UI thread. Call after changing accounts or settings.</summary>
    internal void Publish()
    {
        var slots = Settings.Current.Slots;
        var managed = new Dictionary<nint, int>();
        var numbers = new Dictionary<nint, int>();
        var keys = new Dictionary<KeyBinding, (int, nint)>();
        foreach (var pair in Accounts.Where(p => p.Key <= slots.Count))
        {
            managed[pair.Value.Handle] = pair.Value.Pid;
            numbers[pair.Value.Handle] = pair.Key;
            keys[KeyBinding.Parse(slots[pair.Key - 1].Hotkey)] = (pair.Key, pair.Value.Handle);
        }
        input = new InputState(managed, numbers, keys, KeyBinding.Parse(Settings.MouseToggleHotkey), editing, paused, Settings.MouseFocus);
    }

    /// <summary>Status line shown in the controls and tray menu after a layout or swap.</summary>
    private string LayoutStatus() => $"{Accounts.Count(p => p.Key <= Settings.Current.Slots.Count)} / {Settings.Current.Slots.Count} accounts · {Settings.ActiveProfile}";

    /// <summary>
    /// One line for a running client in the assignment pickers: Newest, then Slot N / Forgotten /
    /// Unassigned, PID and title. Menu callers escape '&amp;' themselves.
    /// </summary>
    internal string Describe(ManagedWindow window, bool newest)
    {
        var assignment = Accounts.FirstOrDefault(p => p.Value.Handle == window.Handle && p.Value.Valid);
        var state = assignment.Value != null ? $"Slot {assignment.Key}" : IsForgotten(window) ? "Forgotten" : "Unassigned";
        return $"{(newest ? "Newest · " : "")}{state} · PID {window.Pid} · {window.Title}";
    }

    internal IReadOnlyList<ManagedWindow> Discover()
    {
        var found = new List<ManagedWindow>();
        var processes = new Dictionary<int, (bool Eligible, long Start)>();
        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window) || Native.GetWindow(window, Native.GwOwner) != 0) return true; // owned dialogs are not clients
            Native.GetWindowThreadProcessId(window, out var rawPid);
            var pid = (int)rawPid;
            if (!processes.TryGetValue(pid, out var info)) processes[pid] = info = ProcessInfo(pid);
            if (!info.Eligible) return true;
            var placement = new Native.Placement { Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.Placement>() };
            if (!Native.GetWindowPlacement(window, ref placement)) return true;
            // A just-created visible window can briefly report SW_HIDE during startup.
            // Restoring that transitional state would hide the window when forgotten.
            if (placement.ShowCmd == 0) placement.ShowCmd = Native.SwShowNormal;
            found.Add(new ManagedWindow { Handle = window, Pid = pid, StartTicks = info.Start,
                Title = Native.Title(window), OriginalStyle = Native.GetWindowLongPtr(window, Native.GwlStyle), OriginalPlacement = placement });
            return true;
        }, 0);
        foreach (var stale in processCache.Keys.Where(pid => !processes.ContainsKey(pid)).ToArray()) processCache.Remove(stale);
        return found.OrderBy(w => w.StartTicks).ThenBy(w => w.Handle).ToList();
    }

    // Process.GetProcessById(...).ProcessName snapshots every process on the system per call.
    // Instead open each window's process once per scan and reuse its eligibility while the
    // creation time (which also guards against PID reuse) is unchanged.
    private (bool Eligible, long Start) ProcessInfo(int pid)
    {
        var handle = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (handle == 0) return (false, 0);
        try
        {
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) return (false, 0);
            var start = DateTime.FromFileTimeUtc(created).Ticks;
            if (processCache.TryGetValue(pid, out var cached) && cached.Created == created) return (cached.Eligible, cached.Eligible ? start : 0);
            var eligible = testProcess?.Invoke(pid) ?? (Native.ProcessName(handle) is { } name && Settings.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase));
            processCache[pid] = (created, eligible);
            return (eligible, eligible ? start : 0);
        }
        finally { Native.CloseHandle(handle); }
    }

    internal void Rescan()
    {
        if (Editing) return;
        try { RescanCore(); } finally { Publish(); }
    }
    private void RescanCore()
    {
        forgotten.RemoveWhere(id => !Native.IsWindow(id.Handle) || Native.GetWindowThreadProcessId(id.Handle, out var pid) == 0 || pid != id.Pid);
        var candidates = Discover().Where(w => !IsForgotten(w)).ToList();
        var changed = false;
        foreach (var pair in Accounts.ToArray())
        {
            // Do not recycle account numbers when another account exits.
            if (!candidates.Any(w => w.Handle == pair.Value.Handle && w.Pid == pair.Value.Pid && w.StartTicks == pair.Value.StartTicks))
            {
                if (pair.Value.Valid) Restore(pair.Value);
                Accounts.Remove(pair.Key);
                if (MainAccount == pair.Key) MainAccount = 1;
                changed = true;
            }
        }
        // Numbered titles from the previous tool are useful on first attachment.
        var unassigned = candidates.Where(w => Accounts.Values.All(a => a.Handle != w.Handle)).ToList();
        foreach (var window in unassigned.ToArray())
        {
            var match = Regex.Match(window.Title, @"^(?:WoW|WOB)\s*(\d+)$", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var number) && number is >= 1 and <= 10 && !Accounts.ContainsKey(number))
            {
                Accounts[number] = window;
                unassigned.Remove(window);
                changed = true;
            }
        }
        foreach (var window in unassigned)
        {
            var free = Enumerable.Range(1, Settings.Current.Slots.Count).FirstOrDefault(n => !Accounts.ContainsKey(n));
            if (free == 0) break;
            Accounts[free] = window;
            changed = true;
        }
        if (changed)
        {
            if (!Paused) ApplyLayout();
            Changed?.Invoke();
        }
    }

    internal int SlotFor(int account, int main) => account == main ? 1 : account == 1 ? main : account;
    /// <summary>Thread-safe: checks the published snapshot and that the HWND still belongs to the same process.</summary>
    internal bool IsManaged(nint window) =>
        window != 0 && input.Managed.TryGetValue(window, out var pid) && Native.GetWindowThreadProcessId(window, out var actual) != 0 && actual == pid;
    internal nint HoverTarget(Native.Point point)
    {
        var root = Native.RootAt(point);
        return IsManaged(root) ? root : 0;
    }
    internal void ApplyLayout(int? main = null, bool swapOnly = false)
    {
        if (!swapOnly) CancelSwaps("explicit layout");
        var targetMain = main ?? MainAccount;
        // Style is recorded only where this call changed it; bounds are null when unreadable.
        var rollback = new List<(ManagedWindow Window, nint? Style, Rectangle? Bounds)>();
        try
        {
            foreach (var pair in Accounts)
            {
                var window = pair.Value;
                if (!window.Valid) continue;
                if (swapOnly && (pair.Key > Settings.Current.Slots.Count || SlotFor(pair.Key, MainAccount) == SlotFor(pair.Key, targetMain))) continue;
                if (pair.Key > Settings.Current.Slots.Count) { Restore(window); continue; }
                var slot = SlotFor(pair.Key, targetMain);
                if (slot < 1 || slot > Settings.Current.Slots.Count) continue;
                Rectangle? before = Native.GetWindowRect(window.Handle, out var rect) ? rect.Rectangle : null;
                var style = Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
                var desiredStyle = Settings.Borderless ? (nint)((long)window.OriginalStyle & ~Native.BorderMask) : window.OriginalStyle;
                var styleChanged = !swapOnly && style != desiredStyle;
                rollback.Add((window, styleChanged ? style : null, before));
                if (styleChanged) Native.SetWindowLongPtr(window.Handle, Native.GwlStyle, desiredStyle);
                if (Native.IsIconic(window.Handle)) Native.ShowWindow(window.Handle, Native.SwShowNoActivate);
                var bounds = Settings.Current.Slots[slot - 1].Bounds;
                if (swapOnly || styleChanged || before != bounds)
                {
                    if (swapOnly)
                    {
                        TraceSwap($"Swap #{issuingSwapId} move: account {pair.Key}, PID {window.Pid}, {before?.ToString() ?? "unreadable"} -> {bounds}, style 0x{(long)style:X8}.");
                        PositionSwapWindow(window.Handle, bounds);
                    }
                    else Native.Position(window.Handle, bounds, styleChanged);
                    recordedMoves?.Add((window.Handle, bounds));
                }
            }
            MainAccount = targetMain;
            if (!swapOnly) observedDrift.Clear();
            status = LayoutStatus();
        }
        catch
        {
            // A swap never writes styles, so its rollback moves geometry back through the same
            // asynchronous, frame-preserving path. Explicit layouts restore what they changed.
            foreach (var item in rollback.Where(r => r.Window.Valid))
            {
                try
                {
                    if (item.Style is { } style) Native.SetWindowLongPtr(item.Window.Handle, Native.GwlStyle, style);
                    if (item.Bounds is not { } bounds) continue;
                    if (swapOnly) PositionSwapWindow(item.Window.Handle, bounds);
                    else Native.Position(item.Window.Handle, bounds, frameChanged: item.Style != null);
                }
                catch { /* keep the original failure */ }
            }
            throw;
        }
        finally { Publish(); }
    }
    internal bool Swap(int account, bool requireGameForeground = false)
    {
        if (Paused || Editing || account < 1 || account > Settings.Current.Slots.Count || !Accounts.TryGetValue(account, out var window) || !window.Valid) return false;
        var request = new SwapRequest(++swapSequence, account, window, SwapClock(), requireGameForeground);
        TraceSwap($"Swap #{request.Id} requested: account {account}, PID {window.Pid}, current main {MainAccount}.");
        if (activeSwap is { } active)
        {
            if (active.TimedOut)
            {
                TraceSwap($"Swap #{request.Id} rejected: swap #{active.Request.Id} still has unsettled moves after timeout.");
                return false;
            }
            TraceSwap($"Swap #{request.Id} queued behind #{active.Request.Id}; replaces {(queuedSwap is { } queued ? $"#{queued.Id}" : "none")}.");
            queuedSwap = request;
            return true;
        }
        StartSwap(request);
        return true;
    }
    private bool RequestValid(SwapRequest request) => !Paused && !Editing
        && request.Account <= Settings.Current.Slots.Count
        && Accounts.TryGetValue(request.Account, out var current)
        && ReferenceEquals(current, request.Window) && current.Valid
        && (!request.RequireGameForeground || IsManaged(ReadForeground()));

    private void StartSwap(SwapRequest request)
    {
        if (!RequestValid(request)) { TraceSwap($"Swap #{request.Id} cancelled: target unavailable or management suspended."); return; }
        var startedAt = SwapClock();
        TraceSwap($"Swap #{request.Id} starting: main {MainAccount} -> {request.Account}, queued {startedAt - request.RequestedAt} ms.");
        issuingSwapId = request.Id;
        Native.Point? anchor;
        lock (focusLock) { anchor = mouseAtSwap = ReadPointer(); lastHover = 0; }
        // Activating the new main reorders windows, and Windows then re-checks the window under the
        // stationary pointer: still the old main. With tracking on it would take focus straight back,
        // and keep it as it shrinks into the new main's old slot. Hold tracking off until the mouse moves.
        // Games apply the asynchronous resize a frame or more later; until then the old main is
        // still the big window under the pointer, and any hover focus would hand it focus again.
        // Both holds are in place before the activation request reaches the focus worker.
        settling = new Settling(Environment.TickCount64 + SettleTimeoutMs, null);
        recordedMoves = [];
        try
        {
            HoldTracking(anchor);
            var handle = request.Window.Handle;
            if (RequestActivation is { } queue) { var generation = ActivationGeneration; queue(() => ActivateQueued(handle, generation)); }
            else Activate(handle);
            ApplyLayout(request.Account, swapOnly: true);
            var targets = recordedMoves.ToArray();
            activeSwap = targets.Length == 0 ? null : new SwapTransition(request, startedAt, targets);
            swapWaiting = activeSwap != null;
            settling = new Settling(Environment.TickCount64 + SettleTimeoutMs, targets);
            TraceSwap($"Swap #{request.Id} issued: {targets.Length} moves, main {MainAccount}, {SwapClock() - startedAt} ms since start.");
        }
        catch
        {
            CancelSwaps($"swap #{request.Id} failed");
            throw;
        }
        finally { recordedMoves = null; }
        Changed?.Invoke();
    }

    /// <summary>UI-thread poll: observes moves, then starts at most one latest queued selection. Never repairs drift.</summary>
    internal void ProcessSwaps()
    {
        if (activeSwap is not { } active) return;
        var now = SwapClock();
        var allAtTarget = true;
        foreach (var target in active.Targets)
        {
            // An HWND disappearing or changing owners must not release a deferred account selection.
            if (!Accounts.Values.Any(w => w.Handle == target.Handle && w.Valid))
            {
                CancelSwaps($"swap #{active.Request.Id} lost a managed window");
                return;
            }
            var actual = ReadBounds(target.Handle);
            if (!active.Observed.TryGetValue(target.Handle, out var before) || before != actual)
            {
                active.Observed[target.Handle] = actual;
                TraceSwap($"Swap #{active.Request.Id} observed HWND {target.Handle}: {actual?.ToString() ?? "unavailable"}, expected {target.Bounds}, elapsed {now - active.StartedAt} ms.");
            }
            allAtTarget &= actual == target.Bounds;
        }
        if (allAtTarget) active.AtTargetSince ??= now;
        else active.AtTargetSince = null;
        if (active.AtTargetSince is { } since && now - since >= SwapQuietMs)
        {
            TraceSwap($"Swap #{active.Request.Id} geometry settled: main {MainAccount}, elapsed {now - active.StartedAt} ms, quiet {now - since} ms{(active.TimedOut ? ", after timeout; no queued replay" : "")}.");
            activeSwap = null;
            swapWaiting = false;
            if (active.TimedOut)
            {
                status = LayoutStatus();
                Changed?.Invoke();
            }
            var next = queuedSwap;
            queuedSwap = null;
            if (next != null) StartSwap(next);
        }
        else if (!active.TimedOut && now - active.StartedAt >= SwapTimeoutMs)
        {
            active.TimedOut = true;
            CancelQueuedSwap($"swap #{active.Request.Id} timed out");
            status = "Swap did not settle — further swaps wait for the pending moves; no automatic resize retry";
            TraceSwap($"Swap #{active.Request.Id} timed out after {now - active.StartedAt} ms; blocking new swaps until its geometry settles. No resize retry.");
            Changed?.Invoke();
        }
    }
    private void CancelQueuedSwap(string reason)
    {
        if (queuedSwap is { } queued) TraceSwap($"Swap #{queued.Id} cancelled: {reason}.");
        queuedSwap = null;
    }
    private void CancelSwaps(string reason)
    {
        CancelQueuedSwap(reason);
        if (activeSwap is { } active) TraceSwap($"Swap #{active.Request.Id} observation cancelled: {reason}.");
        activeSwap = null;
        swapWaiting = false;
        settling = null;
        Interlocked.Increment(ref activationGeneration);
    }
    internal void ResetLayout() { ApplyLayout(1); Changed?.Invoke(); }
    /// <summary>
    /// Reports game-driven layout changes without touching client geometry or styles.
    /// Automatic frame/size corrections can cause WoW to repeatedly resize itself.
    /// UI thread only; explicit swaps and layout actions own all placement writes.
    /// </summary>
    internal void ObserveLayout()
    {
        if (Paused || Editing || MouseInputHeld() || LayoutSettling()) return;
        foreach (var stale in observedDrift.Keys.Where(h => !Accounts.Values.Any(w => w.Handle == h)).ToArray()) observedDrift.Remove(stale);
        foreach (var pair in Accounts)
        {
            var window = pair.Value;
            if (pair.Key > Settings.Current.Slots.Count || !window.Valid) continue;
            if (Native.IsIconic(window.Handle) || Native.IsHungAppWindow(window.Handle)) continue;
            var slot = SlotFor(pair.Key, MainAccount);
            if (slot < 1 || slot > Settings.Current.Slots.Count) continue;
            var bounds = Settings.Current.Slots[slot - 1].Bounds;
            if (ReadBounds(window.Handle) is not { } actual) continue;
            var style = (long)Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
            var frame = (Settings.Borderless ? 0 : (long)window.OriginalStyle) & Native.BorderMask;
            var zoomed = Native.IsZoomed(window.Handle);
            var styleWrong = (style & Native.BorderMask) != frame;
            if (actual == bounds && !styleWrong && !zoomed)
            {
                observedDrift.Remove(window.Handle);
                continue;
            }
            var observation = (actual, style & Native.BorderMask, zoomed, slot);
            if (observedDrift.TryGetValue(window.Handle, out var previous) && previous == observation) continue;
            observedDrift[window.Handle] = observation;
            Program.LogLine($"Observed layout drift: account {pair.Key}, PID {window.Pid}, main {MainAccount}, slot {slot}: {actual} style 0x{style:X8}{(zoomed ? " maximized" : "")}; expected {bounds}. No automatic correction.");
        }
    }
    internal void SwitchProfile(string name)
    {
        var previous = Settings.ActiveProfile;
        Settings.ActiveProfile = name;
        try { ApplyLayout(1); }
        catch { Settings.ActiveProfile = previous; throw; }
        Rescan();
        Changed?.Invoke();
    }
    internal void Assign(int account, nint handle)
    {
        if (account < 1 || account > Settings.Current.Slots.Count) throw new ArgumentOutOfRangeException(nameof(account), "Choose a slot in the active layout.");
        var other = Accounts.FirstOrDefault(p => p.Value.Handle == handle);
        var window = other.Value ?? Discover().FirstOrDefault(w => w.Handle == handle)
            ?? throw new InvalidOperationException("That window has closed.");
        if (!window.Valid) throw new InvalidOperationException("That window has closed. Reopen settings to refresh the list.");
        Accounts.TryGetValue(account, out var displaced);
        if (displaced?.Handle == handle) return;
        if (displaced?.Valid != true) displaced = null;
        forgotten.Remove(Identity(window));
        if (other.Value != null) Accounts.Remove(other.Key);
        if (displaced != null && displaced.Handle != handle)
        {
            if (other.Value != null) Accounts[other.Key] = displaced;
            else { forgotten.Add(Identity(displaced)); Restore(displaced); }
        }
        Accounts[account] = window;
        ResetLayout();
    }
    internal bool Forget(int account)
    {
        if (!Accounts.Remove(account, out var window)) return false;
        forgotten.Add(Identity(window));
        Restore(window);
        ResetLayout();
        return true;
    }
    internal void SaveCurrentPositions()
    {
        var draft = Settings.Copy();
        foreach (var pair in Accounts.Where(p => p.Key <= Settings.Current.Slots.Count && p.Value.Valid))
        {
            if (Native.IsIconic(pair.Value.Handle)) throw new InvalidOperationException("Restore minimized WoW windows before saving the layout.");
            if (!Native.GetWindowRect(pair.Value.Handle, out var rectangle)) throw new InvalidOperationException("Could not read a game window position.");
            var slot = draft.Current.Slots[SlotFor(pair.Key, MainAccount) - 1];
            slot.X = rectangle.Left; slot.Y = rectangle.Top;
            slot.Width = rectangle.Right - rectangle.Left; slot.Height = rectangle.Bottom - rectangle.Top;
        }
        draft.Validate(); Settings = draft;
    }

    /// <summary>
    /// True while windows moved by the last swap have not reached their new slots, capped at
    /// <see cref="SettleTimeoutMs"/> (thread-safe). Clears the settling state once it is over,
    /// so it is a query with a side effect. Reads window rectangles: never call it under trackingLock.
    /// </summary>
    internal bool LayoutSettling()
    {
        var current = settling;
        if (current == null) return false;
        var done = Environment.TickCount64 >= current.Deadline
            || !swapWaiting && current.Targets is { } targets && targets.All(t => !Native.IsWindow(t.Handle) || ReadBounds(t.Handle) == t.Bounds);
        if (done) Interlocked.CompareExchange(ref settling, null, current);
        return !done;
    }
    /// <summary>
    /// Runs in the low-level mouse hook, before Windows processes the movement: Windows'
    /// own tracking is on only while the new position is over an exposed managed window,
    /// so it can never activate another app. Window and rectangle reads happen before
    /// trackingLock; the locked section only rechecks published state and makes one system call.
    /// </summary>
    internal void PointerMoved(Native.Point point)
    {
        if (SystemTracking is not { } setTracking) return;
        var state = input;
        var settlingNow = LayoutSettling();
        var wanted = !settlingNow && state.MouseFocus && !state.Paused && !state.Editing && !MouseInputHeld() && HoverTarget(point) != 0;
        lock (trackingLock)
        {
            // LayoutSettling() already cleared a finished settle, so a non-null value here is a swap
            // published since the read above: keep tracking off without reading rectangles again.
            if (settlingNow || settling != null)
            {
                if (trackingOn) { setTracking(false); trackingOn = false; }
                return;
            }
            if (trackingHeldAt is { } held)
            {
                if (held.X == point.X && held.Y == point.Y) return;
                trackingHeldAt = null;
            }
            if (wanted == trackingOn) return;
            // A refused change (tracking failed or unavailable) leaves hover to the app's own activation.
            trackingOn = setTracking(wanted) && wanted;
        }
    }
    /// <summary>UI thread: turns Windows' tracking off until the pointer leaves <paramref name="anchor"/>.</summary>
    private void HoldTracking(Native.Point? anchor)
    {
        if (SystemTracking is not { } setTracking) return;
        lock (trackingLock)
        {
            trackingHeldAt = anchor ?? new Native.Point(int.MinValue, int.MinValue);
            if (!trackingOn) return;
            setTracking(false);
            trackingOn = false;
        }
    }
    /// <summary>Hover-focus decision. Thread-safe; production calls it on the focus worker so slow activation never delays input hooks.</summary>
    internal void MouseTick()
    {
        var state = input;
        if (state.Paused || state.Editing || !state.MouseFocus) { MouseDecision = "Paused"; return; }
        // Do not transfer a held mouse button to another game window mid-drag.
        if (MouseInputHeld()) { MouseDecision = "Held input"; return; }
        if (ReadPointer() is not { } point) { MouseDecision = "No cursor"; return; }
        if (LayoutSettling()) { MouseDecision = "Layout settling"; return; }
        nint target;
        bool focused;
        lock (focusLock)
        {
            if (mouseAtSwap is { } anchor && point.X == anchor.X && point.Y == anchor.Y) { MouseDecision = "Stationary after swap"; return; }
            mouseAtSwap = null;
            target = HoverTarget(point);
            if (target == 0) { MouseDecision = "Outside"; lastHover = 0; return; }
            focused = Native.GetForegroundWindow() == target;
            if (target != lastHover) hoverSince = Environment.TickCount64;
            if (focused) MouseDecision = "Already focused";
            // Windows' tracking normally activates the window first; step in only if it did not.
            else if (trackingOn && Environment.TickCount64 - hoverSince < TrackingGraceMs) { MouseDecision = "Windows tracking"; lastHover = target; return; }
            else if (target == lastHover && Environment.TickCount64 < retryFocusAfter) { MouseDecision = "Throttled"; return; }
            else
            {
                MouseDecision = $"Activating {target}";
                retryFocusAfter = Environment.TickCount64 + 25;
            }
            lastHover = target;
        }
        if (focused) { SetFocusRefused(false); return; }
        if (trackingOn) Program.LogLine($"Windows tracking did not activate account {(input.Numbers.TryGetValue(target, out var n) ? n : 0)} within {TrackingGraceMs} ms; activating directly.");
        Activate(target);
    }
    /// <summary>
    /// Focus worker: runs an activation queued by <see cref="RequestActivation"/>. Revalidates the
    /// target against the current snapshot and the generation captured at request time, because
    /// pause, Settings, a reassignment, an explicit layout or a profile switch may have happened
    /// while the worker was busy.
    /// </summary>
    internal void ActivateQueued(nint target, long generation)
    {
        var state = input;
        if (state.Paused || state.Editing || generation != ActivationGeneration || !IsManaged(target)) return;
        Activate(target);
    }
    private void Activate(nint target)
    {
        if (Native.GetForegroundWindow() == target) return;
        var timer = Stopwatch.StartNew();
        var activated = ActivateWindow(target);
        // Timing evidence for clients that answer focus slowly; normal activation stays silent.
        if (timer.ElapsedMilliseconds >= 40)
        {
            var state = input;
            var account = state.Numbers.TryGetValue(target, out var number) ? $"account {number}" : "unassigned window";
            var pid = state.Managed.TryGetValue(target, out var id) ? id : 0;
            Program.LogLine($"Focus {(activated ? "took" : "failed after")} {timer.ElapsedMilliseconds} ms: {account}, PID {pid}, \"{Native.Title(target)}\"");
        }
        SetFocusRefused(!activated);
    }
    private void SetFocusRefused(bool refused)
    {
        if (focusRefused == refused) return;
        focusRefused = refused;
        FocusWarningChanged?.Invoke();
    }
    private static void Restore(ManagedWindow window)
    {
        if (!window.Valid) return;
        Native.SetWindowLongPtr(window.Handle, Native.GwlStyle, window.OriginalStyle);
        Native.SetWindowPos(window.Handle, 0, 0, 0, 0, 0, Native.NoActivate | Native.NoZOrder | Native.FrameChanged | Native.SwpNoSize | Native.SwpNoMove);
        var placement = window.OriginalPlacement;
        Native.SetWindowPlacement(window.Handle, ref placement);
        if (((long)window.OriginalStyle & Native.WsVisible) != 0 && !Native.IsWindowVisible(window.Handle))
            Native.ShowWindow(window.Handle, placement.ShowCmd switch
            {
                Native.SwShowMinimized => Native.SwShowMinNoActive,
                Native.SwShowMaximized => Native.SwShowMaximized,
                _ => Native.SwShowNa,
            });
    }
    public void Dispose() { CancelSwaps("exit"); foreach (var window in Accounts.Values) Restore(window); Accounts.Clear(); }
}
