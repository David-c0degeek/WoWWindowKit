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

internal sealed class WindowManager : IDisposable
{
    private AppSettings settings;
    private bool paused, editing;
    private volatile InputState input = new(new Dictionary<nint, int>(), new Dictionary<nint, int>(), new Dictionary<KeyBinding, (int, nint)>(), default, false, false, false);
    private volatile bool focusRefused;
    private string status = "Waiting for WoW windows";
    private readonly object focusLock = new();
    private readonly Dictionary<int, (long Created, bool Eligible)> processCache = [];
    internal AppSettings Settings { get => settings; set { settings = value; processCache.Clear(); Publish(); } }
    internal Dictionary<int, ManagedWindow> Accounts { get; } = [];
    internal int MainAccount { get; private set; } = 1;
    internal bool Paused { get => paused; set { paused = value; Publish(); } }
    internal bool Editing { get => editing; set { editing = value; Publish(); } }
    internal string Status => focusRefused ? "Windows refused focus — click a WoW window once, then retry" : status;
    internal string MouseDecision { get; private set; } = "Idle";
    internal InputState Input => input;
    internal Action? Changed;
    /// <summary>Raised on the thread that activated a window; owners must marshal UI work.</summary>
    internal Action? FocusWarningChanged;
    internal Func<Native.Point?> ReadPointer = static () => Native.GetCursorPos(out var point) ? point : null;
    internal Func<bool> MouseInputHeld = static () => Native.Down(1) || Native.Down(2) || Native.Down(4) || Native.Down(5) || Native.Down(6);
    internal Func<nint, bool> ActivateWindow = Native.Activate;
    /// <summary>Turns Windows' active window tracking on or off; null leaves the system untouched (tests).</summary>
    internal Action<bool>? SystemTracking;
    /// <summary>How long Windows gets to activate a hovered window before the app does it itself.</summary>
    internal int TrackingGraceMs = 150;
    private volatile bool trackingOn;
    private long hoverSince;
    private Native.Point? mouseAtSwap;
    private nint lastHover;
    private long retryFocusAfter;
    private readonly Func<int, bool>? testProcess;
    private readonly HashSet<(nint Handle, int Pid, long Start)> forgotten = [];
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

    internal IReadOnlyList<ManagedWindow> Discover()
    {
        var found = new List<ManagedWindow>();
        var processes = new Dictionary<int, (bool Eligible, long Start)>();
        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window) || Native.GetWindow(window, 4) != 0) return true; // owned dialogs are not clients
            Native.GetWindowThreadProcessId(window, out var rawPid);
            var pid = (int)rawPid;
            if (!processes.TryGetValue(pid, out var info)) processes[pid] = info = ProcessInfo(pid);
            if (!info.Eligible) return true;
            var placement = new Native.Placement { Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.Placement>() };
            if (!Native.GetWindowPlacement(window, ref placement)) return true;
            // A just-created visible window can briefly report SW_HIDE during startup.
            // Restoring that transitional state would hide the window when forgotten.
            if (placement.ShowCmd == 0) placement.ShowCmd = 1;
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
        var handle = Native.OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
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
        var targetMain = main ?? MainAccount;
        var rollback = new List<(ManagedWindow Window, nint Style, Rectangle Bounds)>();
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
                Native.GetWindowRect(window.Handle, out var before);
                rollback.Add((window, Native.GetWindowLongPtr(window.Handle, Native.GwlStyle), before.Rectangle));
                var style = Native.GetWindowLongPtr(window.Handle, Native.GwlStyle);
                var desiredStyle = Settings.Borderless ? (nint)((long)window.OriginalStyle & ~Native.BorderMask) : window.OriginalStyle;
                var styleChanged = !swapOnly && style != desiredStyle;
                if (styleChanged) Native.SetWindowLongPtr(window.Handle, Native.GwlStyle, desiredStyle);
                if (Native.IsIconic(window.Handle)) Native.ShowWindow(window.Handle, 4); // show without activation
                var bounds = Settings.Current.Slots[slot - 1].Bounds;
                if (swapOnly || styleChanged || before.Rectangle != bounds)
                    Native.Position(window.Handle, bounds, styleChanged, asynchronous: swapOnly);
            }
            MainAccount = targetMain;
            status = $"{Accounts.Count(p => p.Key <= Settings.Current.Slots.Count)} / {Settings.Current.Slots.Count} accounts · {Settings.ActiveProfile}";
        }
        catch
        {
            foreach (var item in rollback.Where(r => r.Window.Valid))
            {
                Native.SetWindowLongPtr(item.Window.Handle, Native.GwlStyle, item.Style);
                try { Native.Position(item.Window.Handle, item.Bounds); } catch { /* keep the original failure */ }
            }
            throw;
        }
        finally { Publish(); }
    }
    internal bool Swap(int account)
    {
        if (Paused || Editing || account < 1 || account > Settings.Current.Slots.Count || !Accounts.TryGetValue(account, out var window) || !window.Valid) return false;
        lock (focusLock) { mouseAtSwap = ReadPointer(); lastHover = 0; }
        Activate(window.Handle);
        ApplyLayout(account, swapOnly: true);
        Changed?.Invoke();
        return true;
    }
    internal void ResetLayout() { ApplyLayout(1); Changed?.Invoke(); }
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
    /// Runs in the low-level mouse hook, before Windows processes the movement: Windows'
    /// own tracking is on only while the new position is over an exposed managed window,
    /// so it can never activate another app.
    /// </summary>
    internal void PointerMoved(Native.Point point)
    {
        if (SystemTracking == null) return;
        var state = input;
        var on = state.MouseFocus && !state.Paused && !state.Editing && !MouseInputHeld() && HoverTarget(point) != 0;
        if (on == trackingOn) return;
        SystemTracking(on);
        trackingOn = on;
    }
    /// <summary>Hover-focus decision. Thread-safe; production calls it on the focus worker so slow activation never delays input hooks.</summary>
    internal void MouseTick()
    {
        var state = input;
        if (state.Paused || state.Editing || !state.MouseFocus) { MouseDecision = "Paused"; return; }
        // Do not transfer a held mouse button to another game window mid-drag.
        if (MouseInputHeld()) { MouseDecision = "Held input"; return; }
        if (ReadPointer() is not { } point) { MouseDecision = "No cursor"; return; }
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
        Native.SetWindowPos(window.Handle, 0, 0, 0, 0, 0, Native.NoActivate | Native.NoZOrder | Native.FrameChanged | 0x1 | 0x2);
        var placement = window.OriginalPlacement;
        Native.SetWindowPlacement(window.Handle, ref placement);
        if (((long)window.OriginalStyle & 0x10000000) != 0 && !Native.IsWindowVisible(window.Handle))
            Native.ShowWindow(window.Handle, placement.ShowCmd == 2 ? 7 : placement.ShowCmd == 3 ? 3 : 8);
    }
    public void Dispose() { foreach (var window in Accounts.Values) Restore(window); Accounts.Clear(); }
}
