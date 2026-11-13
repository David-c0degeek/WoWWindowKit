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

internal sealed class WindowManager : IDisposable
{
    internal AppSettings Settings { get; set; }
    internal Dictionary<int, ManagedWindow> Accounts { get; } = [];
    internal int MainAccount { get; private set; } = 1;
    internal bool Paused { get; set; }
    internal bool Editing { get; set; }
    internal string Status { get; private set; } = "Waiting for WoW windows";
    internal string MouseDecision { get; private set; } = "Idle";
    internal Action? Changed;
    internal Func<Native.Point?> ReadPointer = static () => Native.GetCursorPos(out var point) ? point : null;
    internal Func<bool> MouseInputHeld = static () => Native.Down(1) || Native.Down(2) || Native.Down(4) || Native.Down(5) || Native.Down(6);
    internal Func<nint, bool> ActivateWindow = Native.Activate;
    private Native.Point? mouseAtSwap;
    private nint lastHover;
    private long retryFocusAfter;
    private readonly Func<int, bool>? testProcess;
    private readonly HashSet<(nint Handle, int Pid, long Start)> forgotten = [];
    private static (nint Handle, int Pid, long Start) Identity(ManagedWindow window) => (window.Handle, window.Pid, window.StartTicks);
    internal bool IsForgotten(ManagedWindow window) => forgotten.Contains(Identity(window));
    internal WindowManager(AppSettings settings, Func<int, bool>? testProcess = null) { Settings = settings; this.testProcess = testProcess; }

    internal IReadOnlyList<ManagedWindow> Discover()
    {
        var found = new List<ManagedWindow>();
        var processes = new Dictionary<int, (bool Eligible, long Start)>();
        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window) || Native.GetWindow(window, 4) != 0) return true; // owned dialogs are not clients
            Native.GetWindowThreadProcessId(window, out var rawPid);
            var pid = (int)rawPid;
            if (!processes.TryGetValue(pid, out var info))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var eligible = testProcess?.Invoke(pid) ?? Settings.ProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
                    info = (eligible, eligible ? process.StartTime.ToUniversalTime().Ticks : 0);
                }
                catch { info = (false, 0); }
                processes[pid] = info;
            }
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
        return found.OrderBy(w => w.StartTicks).ThenBy(w => w.Handle).ToList();
    }

    internal void Rescan()
    {
        if (Editing) return;
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
    internal bool IsManaged(nint window) => Accounts.Any(p => p.Key <= Settings.Current.Slots.Count && p.Value.Handle == window && p.Value.Valid);
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
            Status = $"{Accounts.Count(p => p.Key <= Settings.Current.Slots.Count)} / {Settings.Current.Slots.Count} accounts · {Settings.ActiveProfile}";
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
    }
    internal bool Swap(int account)
    {
        if (Paused || Editing || account < 1 || account > Settings.Current.Slots.Count || !Accounts.TryGetValue(account, out var window) || !window.Valid) return false;
        mouseAtSwap = ReadPointer();
        lastHover = 0;
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
    internal void MouseTick()
    {
        if (Paused || Editing || !Settings.MouseFocus) { MouseDecision = "Paused"; return; }
        // Do not transfer a held mouse button to another game window mid-drag.
        if (MouseInputHeld()) { MouseDecision = "Held input"; return; }
        if (ReadPointer() is not { } point) { MouseDecision = "No cursor"; return; }
        if (mouseAtSwap is { } anchor && point.X == anchor.X && point.Y == anchor.Y) { MouseDecision = "Stationary after swap"; return; }
        mouseAtSwap = null;
        var target = HoverTarget(point);
        if (target == 0) { MouseDecision = "Outside"; lastHover = 0; return; }
        if (Native.GetForegroundWindow() == target) { ClearFocusFailure(); MouseDecision = "Already focused"; lastHover = target; return; }
        if (target == lastHover && Environment.TickCount64 < retryFocusAfter) { MouseDecision = "Throttled"; return; }
        MouseDecision = $"Activating {target}";
        lastHover = target;
        retryFocusAfter = Environment.TickCount64 + 25;
        Activate(target);
    }
    private void Activate(nint target)
    {
        if (Native.GetForegroundWindow() == target) return;
        if (!ActivateWindow(target))
        {
            Status = "Windows refused focus — click a WoW window once, then retry";
            Changed?.Invoke();
        }
        else ClearFocusFailure();
    }
    private void ClearFocusFailure()
    {
        if (!Status.StartsWith("Windows refused focus", StringComparison.Ordinal)) return;
        Status = $"{Accounts.Count(p => p.Key <= Settings.Current.Slots.Count)} / {Settings.Current.Slots.Count} accounts · {Settings.ActiveProfile}";
        Changed?.Invoke();
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
