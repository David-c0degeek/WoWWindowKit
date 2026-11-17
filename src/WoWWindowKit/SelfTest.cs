using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WoWWindowKit;

/// <summary>
/// In-process integration suite (<c>--self-test [group,group…]</c>). A bare <c>--self-test</c>
/// runs every group in order. <c>settings</c> and <c>tracking-marker</c> need no windows. Every
/// other group first builds the shared mock-window fixture (three separate client processes,
/// 5-man layout) and so can also run alone; in a full run later groups inherit the state that
/// earlier ones leave behind (extra clients, the 10-man profile, edited settings), which their
/// checks tolerate. <c>restore</c> always runs last whenever the fixture was built.
/// </summary>
internal static class SelfTest
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct TestMouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(20)] public uint Flags;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, ref TestMouseInput input, int size);
    private const uint MouseEventMove = 1;
    private static readonly List<string> results = [];
    private static readonly List<Process> children = [];
    private static Action? pumpSwaps;

    private static readonly (string Name, Action<Context> Run)[] Groups =
    [
        ("settings", SettingsGroup),
        ("tracking-marker", TrackingMarkerGroup),
        ("swaps", SwapsGroup),
        ("hotkeys", HotkeysGroup),
        ("hover", HoverGroup),
        ("activation", ActivationGroup),
        ("assignment", AssignmentGroup),
        ("controls-ui", ControlsUiGroup),
        ("hooks", HooksGroup),
        ("live-tracking", LiveTrackingGroup),
        ("restart", RestartGroup),
    ];

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        results.Add("PASS: " + name);
    }
    private static void Pump(int milliseconds = 80)
    {
        var stop = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < stop) { Application.DoEvents(); pumpSwaps?.Invoke(); Thread.Sleep(10); }
    }
    private static Process StartClient(int number)
    {
        var exe = Environment.ProcessPath!;
        var process = Process.Start(new ProcessStartInfo(exe, $"--test-client {number}") { UseShellExecute = false })!;
        children.Add(process);
        var until = Environment.TickCount64 + 10000;
        do
        {
            process.Refresh();
            if (process.MainWindowHandle != 0) return process;
            Pump(40);
        } while (!process.HasExited && Environment.TickCount64 < until);
        throw new InvalidOperationException($"Test client did not start. Exited={process.HasExited}, exit code={(process.HasExited ? process.ExitCode : 0)}");
    }
    private static void Expose(nint window) => Native.SetWindowPos(window, Native.HwndTopMost, 0, 0, 0, 0, Native.NoActivate | Native.SwpNoSize | Native.SwpNoMove);
    private static void Unexpose(nint window) => Native.SetWindowPos(window, Native.HwndNoTopMost, 0, 0, 0, 0, Native.NoActivate | Native.SwpNoSize | Native.SwpNoMove);
    private static Keys ToKeys(KeyBinding binding) => (Keys)binding.Key | (binding.Ctrl ? Keys.Control : 0) | (binding.Shift ? Keys.Shift : 0) | (binding.Alt ? Keys.Alt : 0);
    private static void MoveMouseBy(int dx)
    {
        var input = new TestMouseInput { Type = 0, X = dx, Flags = MouseEventMove };
        SendInput(1, ref input, Marshal.SizeOf<TestMouseInput>());
    }

    /// <summary>State shared between groups. Window-dependent groups call <see cref="Windows"/> first.</summary>
    private sealed class Context(bool liveApp, string temporary) : IDisposable
    {
        internal readonly bool LiveApp = liveApp;
        internal readonly string Temporary = temporary;
        internal bool HasWindows => manager != null;
        private WindowManager? manager;
        internal WindowManager Manager => manager!;
        internal AppSettings Imported = null!;
        internal HashSet<int> Eligible = [];
        internal Process One = null!, Two = null!, Three = null!;
        internal nint First, OriginalHandle;
        internal nint OriginalStyle;
        internal Native.Placement InitialPlacement;
        internal Rectangle Working;
        internal Native.Point Point;
        internal Func<Native.Point?> RealPointer = null!;
        internal Func<nint, bool> DefaultActivate = null!;

        /// <summary>Builds the mock-window fixture once, then resets the seams each group may have changed.</summary>
        internal WindowManager Windows()
        {
            if (manager == null)
            {
                Imported = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.default.json"));
                Imported.DisplayName = "A different name";
                // Small real native windows, in separate processes, exercise HWND and foreground boundaries.
                Working = Screen.PrimaryScreen!.WorkingArea;
                foreach (var profile in Imported.Profiles)
                    for (var i = 0; i < profile.Slots.Count; i++)
                    {
                        var slot = profile.Slots[i];
                        slot.X = Working.X + 30 + (i % 5) * 170; slot.Y = Working.Y + 30 + (i / 5) * 170;
                        slot.Width = i == 0 ? 165 : 150; slot.Height = 150;
                    }
                One = StartClient(1); Two = StartClient(2); Three = StartClient(3);
                Eligible = [One.Id, Two.Id, Three.Id];
                manager = new WindowManager(Imported, pid => Eligible.Contains(pid));
                pumpSwaps = manager.ProcessSwaps;
                RealPointer = manager.ReadPointer;
                // Do not steal focus or move the pointer during the user's live session.
                DefaultActivate = LiveApp ? static _ => true : manager.ActivateWindow;
                manager.Rescan(); Pump();
                Check(manager.Accounts.Count == 3 && manager.Accounts[2].Pid == Two.Id, "Separate-process discovery and existing numbered titles");
                First = manager.Accounts[1].Handle;
                OriginalHandle = manager.Accounts[2].Handle;
                InitialPlacement = manager.Accounts[1].OriginalPlacement;
                OriginalStyle = manager.Accounts[1].OriginalStyle;
                Point = new Native.Point(Imported.Current.Slots[0].X + 20, Imported.Current.Slots[0].Y + 20);
            }
            var m = manager;
            m.Paused = false; m.Editing = false;
            // Live user button state must not make the mock-window assertions nondeterministic.
            m.MouseInputHeld = static () => false;
            m.ReadPointer = RealPointer;
            m.ActivateWindow = DefaultActivate;
            m.SystemTracking = null; m.RequestActivation = null;
            return m;
        }

        internal void AssertLayout(int main)
        {
            foreach (var pair in Manager.Accounts.Where(p => p.Key <= Manager.Settings.Current.Slots.Count))
            {
                Native.GetWindowRect(pair.Value.Handle, out var actual);
                var slot = pair.Key == main ? 1 : pair.Key == 1 ? main : pair.Key;
                Check(actual.Rectangle == Manager.Settings.Current.Slots[slot - 1].Bounds, $"Main {main}: account {pair.Key} occupies slot {slot}");
            }
        }

        /// <summary>Restores every managed window; safe to call more than once.</summary>
        public void Dispose() { pumpSwaps = null; manager?.Dispose(); }
    }

    internal static int Run(string? filter)
    {
        var report = Path.Combine(AppContext.BaseDirectory, "self-test-results.txt");
        var liveApp = Mutex.TryOpenExisting(Program.InstanceMutexName, out var liveMutex);
        liveMutex?.Dispose();
        var selected = Groups.Select(g => g.Name).ToList();
        if (filter != null)
        {
            var requested = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = requested.Where(name => !selected.Contains(name)).ToArray();
            if (unknown.Length > 0 || requested.Length == 0)
            {
                File.WriteAllLines(report, [$"Unknown self-test group: {string.Join(", ", unknown)}. Groups: {string.Join(", ", selected)}."]);
                return 2;
            }
            selected = selected.Where(requested.Contains).ToList();
        }
        var temporary = Path.Combine(Path.GetTempPath(), "WoWWindowKit-test-" + Guid.NewGuid().ToString("N"));
        Native.GetCursorPos(out var originalCursor);
        Directory.CreateDirectory(temporary);
        var context = new Context(liveApp, temporary);
        try
        {
            results.Add($"GROUPS: {string.Join(", ", selected)}");
            foreach (var group in Groups.Where(g => selected.Contains(g.Name)))
            {
                results.Add($"GROUP: {group.Name}");
                group.Run(context);
            }
            if (context.HasWindows) RestoreGroup(context);
            results.Add($"SUCCESS: {results.Count(r => r.StartsWith("PASS:", StringComparison.Ordinal))} checks passed; {results.Count(r => r.StartsWith("SKIP:", StringComparison.Ordinal))} test groups skipped.");
            File.WriteAllLines(report, results);
            return 0;
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(report, results); return 1; }
        finally
        {
            pumpSwaps = null;
            // Window-free groups never move the pointer, so they leave it alone too.
            if (!liveApp && context.HasWindows) Native.SetCursorPos(originalCursor.X, originalCursor.Y);
            context.Dispose();
            foreach (var child in children)
            {
                try { if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(1500)) child.Kill(); } } catch { /* best-effort test cleanup */ }
                child.Dispose();
            }
            // Only the explicitly created test scratch folder is removed; keep the results report.
            var resolvedScratch = Path.GetFullPath(temporary);
            var scratchRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WoWWindowKit-test-"));
            if (resolvedScratch.StartsWith(scratchRoot, StringComparison.OrdinalIgnoreCase) && resolvedScratch.Length == scratchRoot.Length + 32)
                Directory.Delete(resolvedScratch, true);
        }
    }

    private static void SettingsGroup(Context context)
    {
        var imported = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.default.json"));
        Check(imported.Profiles.Count == 2 && imported.Profiles[0].Slots.Count == 5 && imported.Profiles[1].Slots.Count == 10, "Both imported profile sizes");
        Check(imported.Profiles[0].Slots[0].Bounds == new Rectangle(0, 0, 1880, 1392) && imported.Profiles[1].Slots[9].Bounds == new Rectangle(2048, 1152, 512, 288), "Imported 5-man and 10-man geometry");
        Check(imported.Profiles[1].Slots.Select(s => s.Hotkey).SequenceEqual(Enumerable.Range(1, 10).Select(n => "F" + n)), "10-man F1–F10 bindings");
        var settingsPath = Path.Combine(context.Temporary, "settings.json");
        imported.Save(settingsPath); imported.DisplayName = "A different name"; imported.Save(settingsPath);
        Check(AppSettings.Load(settingsPath).DisplayName == "A different name" && AppSettings.Load(settingsPath + ".bak").DisplayName == AppSettings.DefaultDisplayName, "Custom app name, atomic save and previous-settings backup");
        Check(!File.ReadAllText(settingsPath).Contains("\"Current\"", StringComparison.Ordinal) && !File.ReadAllText(settingsPath).Contains("\"Bounds\"", StringComparison.Ordinal), "Only user-editable settings serialized");
        var invalid = imported.Copy(); invalid.Current.Slots[1].Hotkey = "F1";
        var rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Duplicate hotkeys rejected");
        Check(KeyBinding.Parse("Ctrl+1") == new KeyBinding((int)Keys.D1, true, false, false) && KeyBinding.Parse("NumPad3").Key == (int)Keys.NumPad3
            && KeyBinding.Parse("shift+F13").Shift && KeyBinding.Parse("Alt+Q").ToString() == "Alt+Q" && KeyBinding.Parse("Ctrl+Shift+1").ToString() == "Ctrl+Shift+1", "Broader hotkeys parse and round-trip");
        var unusable = 0;
        foreach (var text in new[] { "1", "Q", "Shift+Q", "Space", "Escape", "Ctrl+LWin", "65", "Ctrl+A, B", "Ctrl+ShiftKey", "Ctrl+" })
            try { KeyBinding.Parse(text); } catch (InvalidDataException) { unusable++; }
        Check(unusable == 10, "Typing, movement, numeric and modifier-only hotkeys rejected");
        invalid = imported.Copy(); invalid.MouseToggleHotkey = "F2";
        rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Mouse-focus toggle cannot reuse a swap key");
        invalid = imported.Copy(); invalid.Current.Slots[0].Height = 0;
        rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Invalid geometry rejected");
        var custom = imported.Copy(); custom.Profiles.Add(new LayoutProfile { Name = "Raid", Slots = imported.Copy().Current.Slots });
        custom.Validate();
        Check(custom.FindProfile("raid") == "Raid" && custom.FindProfile("10-MAN") == "10-man" && custom.FindProfile("nope") == null, "Launcher profile names resolve to any existing profile, case-insensitively");
        Check(LaunchOptions.Parse(["--profile", "Raid", "--tray"]) == new LaunchOptions(ControlCommand.Tray, "Raid")
            && LaunchOptions.Parse(["--controls"]).Command == ControlCommand.ShowControls && LaunchOptions.Parse([]).Command == ControlCommand.ShowControls
            && LaunchOptions.Parse(["--settings", "--tray"]).Command == ControlCommand.ShowSettings && LaunchOptions.Parse(["--exit", "--settings"]).Command == ControlCommand.Exit,
            "Command line maps to one explicit launch command");
        // The receiver acknowledges accepted commands with 1; the sender treats anything else as failure.
        using (var control = new ControlWindow())
        {
            var received = new List<(ControlCommand, string?)>();
            control.Command = (command, profile) => received.Add((command, profile));
            nint Send(int command, string text)
            {
                var buffer = Marshal.StringToHGlobalUni(text);
                try
                {
                    var data = new Native.CopyData { Data = command, Size = text.Length * sizeof(char), Pointer = buffer };
                    return Native.SendMessageTimeout(control.Handle, Native.WmCopyData, 0, ref data, Native.SmtoAbortIfHung, 2000, out var result) != 0 ? result : -1;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            var longName = new string('r', 300);
            Check(Send((int)ControlCommand.Tray, longName) == 1 && Send(99, "Raid") == 0 && Send((int)ControlCommand.Exit, "") == 1
                && received.SequenceEqual([(ControlCommand.Tray, longName), (ControlCommand.Exit, (string?)null)]),
                "IPC accepts long profile names, rejects unknown commands, and reports acceptance to the sender");
        }
    }

    private static void TrackingMarkerGroup(Context context)
    {
        var markerPath = Path.Combine(context.Temporary, "tracking.restore");
        var system = new WindowTracking.Values(0, 1, 200);
        var applied = 0;
        var crashed = new WindowTracking(markerPath, () => system, v => { system = v; applied++; });
        Check(crashed.Unavailable == null && File.Exists(markerPath) && applied == 0, "Startup records the original tracking values before any change");
        crashed.Set(true); crashed.Set(true);
        Check(system == new WindowTracking.Values(1, 0, 0) && applied == 1, "Enabling tracking applies instant, no-raise values once");
        crashed.Set(false);
        Check(system == new WindowTracking.Values(0, 1, 200), "Disabling restores the user's original tracking values");
        crashed.Set(true); // simulate a crash: never disposed
        var recovered = new WindowTracking(markerPath, () => system, v => system = v);
        Check(system == new WindowTracking.Values(0, 1, 200) && recovered.Original == system && File.ReadAllText(markerPath) == "0 1 200", "Next start repairs tracking left on by a crash");
        recovered.Set(true); recovered.Dispose();
        Check(system == new WindowTracking.Values(0, 1, 200) && !File.Exists(markerPath) && !recovered.Enabled, "Exit restores tracking and removes the marker");
        Check(!recovered.Set(true) && system == new WindowTracking.Values(0, 1, 200), "Tracking stays off after exit");

        // Fault injection: the recovery marker and every system call are checked.
        var blocked = Path.Combine(context.Temporary, "blocked.restore");
        Directory.CreateDirectory(blocked); // a directory where the marker file should go
        applied = 0;
        using (var unwritable = new WindowTracking(blocked, () => system, v => { system = v; applied++; }))
            Check(unwritable.Unavailable != null && !unwritable.Set(true) && applied == 0 && system == new WindowTracking.Values(0, 1, 200),
                "Unwritable recovery marker keeps Windows tracking off for the run");
        using (var unreadable = new WindowTracking(markerPath, () => throw new System.ComponentModel.Win32Exception(5), v => applied++))
            Check(unreadable.Unavailable != null && !unreadable.Set(true) && applied == 0 && !File.Exists(markerPath), "Unreadable system values keep Windows tracking off");

        File.WriteAllText(markerPath, "0 1 200");
        using (var unrepaired = new WindowTracking(markerPath, () => system, _ => throw new System.ComponentModel.Win32Exception(5)))
            Check(unrepaired.Unavailable != null && !unrepaired.Set(true), "A failed crash repair disables tracking for the run");
        Check(File.ReadAllText(markerPath) == "0 1 200", "A failed crash repair keeps the marker for the next start");
        File.Delete(markerPath);

        var failures = 0;
        var refuseEnable = new WindowTracking(markerPath, () => system, v => { if (v.Tracking == 1) { system = system with { ZOrder = 0 }; throw new System.ComponentModel.Win32Exception(5); } system = v; });
        refuseEnable.Failed = _ => failures++;
        Check(!refuseEnable.Set(true) && failures == 1 && system == new WindowTracking.Values(0, 1, 200) && !refuseEnable.RestorePending,
            "A failed enable is reported once and the partial change is rolled back");
        Check(!refuseEnable.Set(true) && failures == 1, "After a failure the hook stops calling into the system");
        refuseEnable.Dispose();
        Check(!File.Exists(markerPath), "A cleanly rolled-back failure removes the marker on exit");

        var broken = false;
        var stuck = new WindowTracking(markerPath, () => system, v => { if (broken) { system = system with { Tracking = 1 }; throw new System.ComponentModel.Win32Exception(5); } system = v; });
        broken = true;
        Check(!stuck.Set(true) && stuck.RestorePending, "A failed restore is remembered");
        stuck.Dispose();
        Check(File.ReadAllText(markerPath) == "0 1 200", "A failed restore on exit keeps the marker");
        broken = false;
        using (var next = new WindowTracking(markerPath, () => system, v => system = v))
            Check(next.Unavailable == null && system == new WindowTracking.Values(0, 1, 200) && next.Original == system, "The next start repairs what the failed restore left behind");

        File.WriteAllText(markerPath, "garbage");
        using (var malformed = new WindowTracking(markerPath, () => system, v => system = v))
            Check(malformed.Unavailable == null && File.ReadAllText(markerPath) == "0 1 200", "A malformed marker is replaced by this run's originals");
        Check(!File.Exists(markerPath), "Disposing an unused tracker removes its marker");
    }

    private static void SwapsGroup(Context context)
    {
        var manager = context.Windows();
        manager.ResetLayout(); Pump();
        context.AssertLayout(1);
        Check(((long)Native.GetWindowLongPtr(manager.Accounts[1].Handle, Native.GwlStyle) & Native.BorderMask) == 0, "Borderless game window style applied");
        foreach (var main in new[] { 2, 3, 2, 1 }) { manager.Swap(main); Pump(); context.AssertLayout(main); }
        Check(manager.Accounts[2].Handle == context.OriginalHandle, "F-key account identity survives repeated swaps");
        // These counters live in the separate mock-client processes, not the manager.
        foreach (var client in manager.Accounts.Values) Native.SendMessage(client.Handle, TestClientForm.CounterMessage, 2, 0);
        manager.Swap(2); Pump();
        Check(Native.SendMessage(manager.Accounts[3].Handle, TestClientForm.CounterMessage, 0, 0) == 0, "Swap leaves unrelated client's native position untouched");
        Check(manager.Accounts.Values.All(w => Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 1, 0) == 0), "Swaps do not rewrite any client's window style");
        foreach (var main in new[] { 3, 1, 2, 3, 2, 1 }) manager.Swap(main);
        Pump(200); context.AssertLayout(1);
        Check(manager.MainAccount == 1, "Rapid async swaps preserve the final logical layout");
        SwapTests.Run(manager, Check);

        // Incident: WoW re-adds frame bits even at correct bounds. Polling must not
        // rewrite that style and trigger another game-driven resize.
        var settle = Environment.TickCount64 + 2000;
        while (manager.LayoutSettling() && Environment.TickCount64 < settle) Pump(20);
        var drifting = manager.Accounts[2].Handle;
        void GameChangesItself(Rectangle bounds)
        {
            var current = (long)Native.GetWindowLongPtr(drifting, Native.GwlStyle);
            Native.SetWindowLongPtr(drifting, Native.GwlStyle, (nint)(current | 0x00050000)); // WS_THICKFRAME | WS_MAXIMIZEBOX
            Native.Position(drifting, bounds);
            Pump();
        }
        void AssertObservationDoesNotWrite(string scenario)
        {
            var before = manager.ReadBounds(drifting);
            var style = Native.GetWindowLongPtr(drifting, Native.GwlStyle);
            var foreground = Native.GetForegroundWindow();
            var zoomed = Native.IsZoomed(drifting);
            Native.SendMessage(drifting, TestClientForm.CounterMessage, 2, 0);
            for (var tick = 0; tick < 8; tick++) { manager.Rescan(); manager.ObserveLayout(); Pump(20); }
            Check(manager.ReadBounds(drifting) == before && Native.GetWindowLongPtr(drifting, Native.GwlStyle) == style
                && Native.IsZoomed(drifting) == zoomed && Native.GetForegroundWindow() == foreground
                && Native.SendMessage(drifting, TestClientForm.CounterMessage, 0, 0) == 0
                && Native.SendMessage(drifting, TestClientForm.CounterMessage, 1, 0) == 0, scenario);
        }
        GameChangesItself(manager.Settings.Current.Slots[1].Bounds);
        AssertObservationDoesNotWrite("Polling leaves game frame bits alone at already-correct slot bounds");
        foreach (var size in new[] { new Rectangle(10, 10, 400, 300), new Rectangle(30, 20, 420, 320), new Rectangle(10, 10, 400, 300) })
        {
            GameChangesItself(size);
            AssertObservationDoesNotWrite("Repeated game-driven drift produces no automatic position, style or focus writes");
        }
        if (!context.LiveApp)
        {
            Native.ShowWindow(drifting, Native.SwShowMaximized); Pump(); // maximized by the game
            AssertObservationDoesNotWrite("Polling leaves a game-maximized window alone");
            Native.ShowWindow(drifting, Native.SwShowNoActivate); Pump();
        }
        manager.ResetLayout(); Pump(); context.AssertLayout(1);
        Check(((long)Native.GetWindowLongPtr(drifting, Native.GwlStyle) & Native.BorderMask) == 0,
            "Explicit Reapply layout still restores borderless slot placement");
        foreach (var main in new[] { 2, 3, 2, 3, 1 }) { manager.Swap(main); manager.ObserveLayout(); }
        Pump(200); context.AssertLayout(1);
        Check(manager.MainAccount == 1, "Rapid swaps with interleaved observation preserve the final layout");
        Check(!manager.Swap(5), "Missing account swap does nothing");
        manager.Paused = true; Check(!manager.Swap(2), "Pause blocks swaps"); manager.Paused = false;
    }

    private static void HotkeysGroup(Context context)
    {
        var manager = context.Windows();
        var originalHandle = context.OriginalHandle;
        var swapCalled = 0; var toggled = 0;
        using var router = new KeyboardHook(manager, n => swapCalled = n, () => toggled++, false);
        Check(!router.HandleKey(new((int)Keys.F2, false, false, false), true, 0), "F-keys pass through outside managed WoW windows");
        Check(!router.HandleKey(new((int)Keys.F2, true, false, false), true, originalHandle), "Removed focus-only Ctrl+F2 passes through");
        Check(router.HandleKey(new((int)Keys.F2, false, false, false), true, originalHandle) && swapCalled == 2, "F2 routes to account 2 inside WoW");
        Check(router.HandleKey(new((int)Keys.F2, false, false, false), false, 0), "Consumed key-up remains consumed after focus changes");
        manager.Editing = true;
        Check(!router.HandleKey(new((int)Keys.F2, false, false, false), true, originalHandle), "Settings editing pauses hotkeys"); manager.Editing = false;
        Check(router.HandleKey(new((int)Keys.F, true, true, false), true, originalHandle) && toggled == 1, "Mouse-focus toggle shortcut");
        manager.Paused = true;
        Check(!router.HandleKey(new((int)Keys.F3, false, false, false), true, originalHandle), "Pause reaches the hook snapshot immediately"); manager.Paused = false;
        // Key routing runs off the UI thread in production; it must work from the published snapshot alone.
        var routed = false;
        var routeThread = new Thread(() => routed = router.HandleKey(new((int)Keys.F3, false, false, false), true, originalHandle) && swapCalled == 3);
        routeThread.Start(); routeThread.Join();
        Check(routed, "F3 routes from a non-UI hook thread");
        var previous = manager.Settings;
        var customized = manager.Settings.Copy();
        customized.Current.Slots[2].Hotkey = "NumPad3"; customized.Current.Slots[1].Hotkey = "alt+2"; customized.MouseToggleHotkey = "Alt+M";
        customized.Validate(); manager.Settings = customized;
        router.HandleKey(new((int)Keys.F3, false, false, false), false, 0); // release keys held by earlier checks
        router.HandleKey(new((int)Keys.F, true, true, false), false, 0);
        Check(router.HandleKey(new((int)Keys.NumPad3, false, false, false), true, originalHandle) && swapCalled == 3, "Custom numpad swap key routes");
        Check(!router.HandleKey(new((int)Keys.F3, false, false, false), true, originalHandle), "Replaced swap key passes through");
        Check(router.HandleKey(new((int)Keys.D2, false, false, true), true, originalHandle) && swapCalled == 2, "Alt+2 combination swap key routes");
        router.HandleKey(new((int)Keys.D2, false, false, true), false, 0);
        Check(!router.HandleKey(new((int)Keys.D2, false, false, false), true, originalHandle) && !router.HandleKey(new((int)Keys.D2, true, false, false), true, originalHandle), "Plain 2 and Ctrl+2 pass through when only Alt+2 is bound");
        Check(router.HandleKey(new((int)Keys.M, false, false, true), true, originalHandle) && toggled == 2, "Custom mouse-focus toggle key");
        Check(!router.HandleKey(new((int)Keys.F, true, true, false), true, originalHandle), "Old toggle key passes through once changed");
        router.HandleKey(new((int)Keys.M, false, false, true), false, 0);
        Check(!router.HandleKey(new((int)Keys.M, false, false, false), true, originalHandle), "Plain letters always pass through");
        manager.Settings = previous;
    }

    private static void HoverGroup(Context context)
    {
        var manager = context.Windows();
        var liveApp = context.LiveApp;
        var imported = context.Imported;
        var first = context.First;
        var point = context.Point;
        manager.ResetLayout(); Pump();
        var scanTimer = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++) manager.Rescan();
        results.Add($"INFO: Rescan averages {scanTimer.Elapsed.TotalMilliseconds / 10:F2} ms with cached process identity.");
        Check(manager.Accounts.Count >= 3 && manager.Accounts[2].Handle == context.OriginalHandle, "Cached process identity keeps assignments across scans");
        Expose(first); Pump();
        Check(manager.HoverTarget(point) == first, "Exposed managed window qualifies for hover focus");
        var simulatedPointer = point;
        manager.ReadPointer = () => simulatedPointer;
        {
            // Windows' tracking is gated in the mouse hook; a fake records what the system would get.
            var requested = new List<bool>();
            manager.SystemTracking = on => { requested.Add(on); return true; };
            manager.PointerMoved(point);
            manager.PointerMoved(new Native.Point(point.X + 1, point.Y));
            Check(requested.SequenceEqual([true]), "Windows tracking turns on once over an exposed managed window");
            manager.PointerMoved(new Native.Point(-30000, -30000));
            Check(requested.SequenceEqual([true, false]), "Windows tracking turns off outside managed windows");
            manager.Paused = true; manager.PointerMoved(point); manager.Paused = false;
            manager.Editing = true; manager.PointerMoved(point); manager.Editing = false;
            var heldInput = manager.MouseInputHeld; manager.MouseInputHeld = static () => true; manager.PointerMoved(point); manager.MouseInputHeld = heldInput;
            Check(requested.SequenceEqual([true, false]), "Pause, Settings and held mouse buttons keep Windows tracking off");
            manager.PointerMoved(point);
            var activations = 0; var activate = manager.ActivateWindow;
            manager.ActivateWindow = _ => { activations++; return true; };
            manager.TrackingGraceMs = 60;
            if (Native.GetForegroundWindow() != first)
            {
                manager.MouseTick();
                Check(manager.MouseDecision == "Windows tracking" && activations == 0, "App leaves activation to Windows while its tracking is on");
                Thread.Sleep(80); manager.MouseTick();
                Check(activations == 1, "App activates directly if Windows' tracking did not within the grace period");
            }
            // Swap under a stationary pointer (activation stubbed): tracking must stay off until real movement.
            requested.Clear();
            manager.Swap(2); Pump(200);
            // Activation is stubbed while the user's app runs; expose only our mock main
            // for the native hit-test without activating any live game window.
            if (liveApp) { Expose(manager.Accounts[2].Handle); Pump(20); }
            manager.PointerMoved(point);
            Check(requested.SequenceEqual([false]), "Keyboard swap holds Windows tracking off under a stationary pointer");
            manager.PointerMoved(point with { X = point.X + 1 });
            Check(requested.SequenceEqual([false, true]), "First real mouse movement after a swap re-enables Windows tracking");
            if (liveApp) Unexpose(manager.Accounts[2].Handle);
            manager.Swap(1); manager.PointerMoved(point with { X = point.X + 2 });
            // Real games resize a frame or more later. Until then the old main is still under the pointer.
            var realBounds = manager.ReadBounds; var stale = true;
            manager.ReadBounds = h => stale ? Rectangle.Empty : realBounds(h);
            Pump(200); requested.Clear();
            manager.Swap(2);
            var activationsBefore = activations; // the swap's own activation is not hover focus
            manager.PointerMoved(point with { X = point.X + 7 }); // sensor jitter during the resize
            Check(manager.LayoutSettling() && !requested.Contains(true), "Mouse movement while swapped windows resize keeps Windows tracking off");
            manager.MouseTick();
            Check(manager.MouseDecision == "Layout settling" && activations == activationsBefore, "App hover focus waits for swapped windows to reach their slots");
            stale = false; Pump(200);
            manager.PointerMoved(point with { X = point.X + 8 });
            Check(!manager.LayoutSettling() && requested.LastOrDefault(), "Hover focus resumes once swapped windows reach their slots");
            stale = true; manager.SettleTimeoutMs = 60;
            manager.Swap(1); Thread.Sleep(80);
            Check(!manager.LayoutSettling(), "A game that never applies its resize cannot block hover focus past the timeout");
            manager.ReadBounds = realBounds; manager.SettleTimeoutMs = 1000; Pump(200);
            manager.PointerMoved(point with { X = point.X + 9 });
            manager.PointerMoved(new Native.Point(-30000, -30000));

            // Tracking that refuses (unavailable or failed) must not leave hover waiting for Windows.
            requested.Clear();
            manager.SystemTracking = on => { requested.Add(on); return false; };
            manager.PointerMoved(point with { X = point.X + 10 });
            if (Native.GetForegroundWindow() != first)
            {
                var before = activations;
                simulatedPointer = point with { X = point.X + 10 };
                manager.MouseTick();
                Check(requested.SequenceEqual([true]) && manager.MouseDecision.StartsWith("Activating", StringComparison.Ordinal) && activations == before + 1,
                    "Refused Windows tracking leaves hover to the app's own activation without a grace delay");
                simulatedPointer = point;
            }
            manager.SystemTracking = null; manager.ActivateWindow = activate; manager.TrackingGraceMs = 150;
        }
        manager.Swap(2); Pump(); manager.MouseTick();
        Check(manager.MouseDecision == "Stationary after swap", "Stationary pointer does not undo a keyboard swap");
        var movement = Stopwatch.StartNew();
        simulatedPointer.X++; manager.MouseTick();
        Check(manager.MouseDecision != "Stationary after swap" && manager.MouseDecision != "Paused", "First mouse movement resumes hover without a 350 ms delay");
        results.Add($"INFO: First movement handled after {movement.Elapsed.TotalMilliseconds:F2} ms (mock windows).");
        manager.ResetLayout(); Pump();
        Unexpose(first);
        using (var blocker = new Form { Text = "Non-game overlap", StartPosition = FormStartPosition.Manual, Bounds = imported.Current.Slots[0].Bounds, TopMost = true, ShowInTaskbar = false })
        {
            blocker.Show(); blocker.Activate();
            Native.SetWindowPos(blocker.Handle, Native.HwndTopMost, point.X - 20, point.Y - 20, 165, 150, 0); Pump();
            Check(manager.HoverTarget(point) == 0, "Overlapping non-game window blocks focus even inside WoW rectangle");
            simulatedPointer = point;
            if (!liveApp) Native.SetCursorPos(point.X, point.Y);
            Pump(100);
            var foreground = Native.GetForegroundWindow();
            manager.MouseTick();
            if (!liveApp) Check(Native.GetForegroundWindow() == foreground, "Mouse focus does not steal focus outside WoW");
            else results.Add("SKIP: Foreground assertions while the user's app is running; simulated focus does not interrupt live clients.");
            Native.SetWindowPos(blocker.Handle, Native.HwndTopMost, context.Working.Right - 250, context.Working.Bottom - 180, 165, 150, 0);
            Expose(first);
            Pump(400);
            var focusDeadline = Environment.TickCount64 + 1500;
            do { manager.MouseTick(); Pump(50); } while (!liveApp && Native.GetForegroundWindow() != first && Environment.TickCount64 < focusDeadline);
            Native.GetCursorPos(out var observed);
            if (!liveApp && Native.GetForegroundWindow() != first)
                throw new InvalidOperationException($"Focus resume failed: {manager.MouseDecision}; expected={first}; hover={manager.HoverTarget(observed)}; {manager.Status}; foreground={Native.Title(Native.GetForegroundWindow())}");
            if (!liveApp) Check(true, "Returning pointer to exposed WoW resumes actual cross-process focus");
            var second = manager.Accounts[2].Handle;
            Expose(second);
            var secondSlot = imported.Current.Slots[1];
            simulatedPointer = new Native.Point(secondSlot.X + 20, secondSlot.Y + 20);
            if (!liveApp) Native.SetCursorPos(secondSlot.X + 20, secondSlot.Y + 20);
            Pump();
            focusDeadline = Environment.TickCount64 + 1500;
            do { manager.MouseTick(); Pump(50); } while (!liveApp && Native.GetForegroundWindow() != second && Environment.TickCount64 < focusDeadline);
            if (!liveApp) Check(Native.GetForegroundWindow() == second, "Mouse hover transfers actual focus between client processes");
            Unexpose(second);
        }
        Unexpose(first);
    }

    private static void ActivationGroup(Context context)
    {
        var manager = context.Windows();
        manager.ResetLayout(); Pump();
        var calls = new List<nint>();
        manager.ActivateWindow = h => { lock (calls) calls.Add(h); return true; };
        using var release = new ManualResetEventSlim();
        using var busy = new ManualResetEventSlim();
        var worker = new FocusWorker(static () => { }, 1000);
        try
        {
            string? ranOn = null;
            using var ran = new ManualResetEventSlim();
            manager.RequestActivation = job => worker.Run(() => { ranOn = Thread.CurrentThread.Name; job(); ran.Set(); });
            // The worker is stuck in an activation of a busy game.
            worker.Run(() => { busy.Set(); release.Wait(5000); });
            Check(busy.Wait(2000), "Focus worker accepted a blocking activation");
            var target = manager.Accounts[2].Handle;
            var timer = Stopwatch.StartNew();
            Check(manager.Swap(2) && timer.ElapsedMilliseconds < 500 && !ran.IsSet,
                $"Keyboard swap returns while the focus worker is blocked in an activation ({timer.ElapsedMilliseconds} ms)");
            release.Set();
            Check(ran.Wait(2000) && ranOn == "Mouse focus" && (calls.Contains(target) || Native.GetForegroundWindow() == target), "The swap's activation runs on the focus worker once it is free");
            Pump(200);

            // An explicit layout (or profile / settings change) while the worker is busy drops the stale request.
            release.Reset(); busy.Reset(); ran.Reset();
            lock (calls) calls.Clear();
            worker.Run(() => { busy.Set(); release.Wait(5000); });
            busy.Wait(2000);
            manager.Swap(3);
            manager.ResetLayout();
            release.Set();
            Check(ran.Wait(2000) && calls.Count == 0, "Reapply layout while an activation is queued drops the stale swap activation");
            Pump(200);

            // Later requests coalesce: only the latest queued activation runs.
            release.Reset(); busy.Reset(); ran.Reset();
            var order = new List<int>();
            worker.Run(() => { busy.Set(); release.Wait(5000); });
            busy.Wait(2000);
            worker.Run(() => { lock (order) order.Add(1); });
            worker.Run(() => { lock (order) order.Add(2); ran.Set(); });
            release.Set();
            ran.Wait(2000); Thread.Sleep(50);
            Check(order.SequenceEqual([2]), "Queued activations coalesce to the latest request");

            // Queued work is revalidated when it runs.
            lock (calls) calls.Clear();
            var generation = manager.ActivationGeneration;
            manager.Paused = true; manager.ActivateQueued(target, generation); manager.Paused = false;
            manager.Editing = true; manager.ActivateQueued(target, generation); manager.Editing = false;
            using (var stranger = new Form { Text = "Not a client", ShowInTaskbar = false })
            {
                stranger.Show(); Pump(20);
                manager.ActivateQueued(stranger.Handle, generation);
            }
            Check(calls.Count == 0, "A queued activation is dropped after pause, Settings, or for an unmanaged window");
        }
        finally
        {
            release.Set();
            worker.Dispose();
            manager.RequestActivation = null;
            manager.ActivateWindow = context.DefaultActivate;
            manager.ResetLayout(); Pump();
        }
    }

    private static void AssignmentGroup(Context context)
    {
        var manager = context.Windows();
        var eligible = context.Eligible;
        var first = context.First;
        manager.ResetLayout(); Pump();
        context.Two.CloseMainWindow(); context.Two.WaitForExit(5000); Pump(); manager.Rescan();
        Check(!manager.Accounts.ContainsKey(2) && manager.Accounts[3].Pid == context.Three.Id, "Client exit leaves a stable empty account slot");
        var replacement = StartClient(2); eligible.Add(replacement.Id); manager.Rescan();
        Check(manager.Accounts[2].Pid == replacement.Id, "Restarted client reclaims the empty account number");
        var forgottenClient = manager.Accounts[2];
        var beforeForgetPlacement = forgottenClient.OriginalPlacement;
        Check(manager.Forget(2), "Forget releases the chosen account");
        Check(!manager.IsManaged(forgottenClient.Handle) && !manager.Input.SwapKeys.Values.Any(v => v.Handle == forgottenClient.Handle), "Forget removes the window from the hook snapshot immediately");
        Pump();
        var releasedPlacement = new Native.Placement { Length = Marshal.SizeOf<Native.Placement>() };
        Native.GetWindowPlacement(forgottenClient.Handle, ref releasedPlacement);
        Check(Native.GetWindowLongPtr(forgottenClient.Handle, Native.GwlStyle) == forgottenClient.OriginalStyle && releasedPlacement.Normal.Rectangle == beforeForgetPlacement.Normal.Rectangle, $"Forget restores original borders and placement (style {Native.GetWindowLongPtr(forgottenClient.Handle, Native.GwlStyle):X}/{forgottenClient.OriginalStyle:X}; show {releasedPlacement.ShowCmd}/{beforeForgetPlacement.ShowCmd}; bounds {releasedPlacement.Normal.Rectangle}/{beforeForgetPlacement.Normal.Rectangle})");
        manager.Rescan(); manager.Rescan();
        Check(!manager.Accounts.ContainsKey(2) && !manager.IsManaged(forgottenClient.Handle) && manager.IsForgotten(forgottenClient), "Repeated rescans never recapture a forgotten window");
        manager.Assign(4, forgottenClient.Handle); Pump();
        Check(manager.Accounts[4].Pid == replacement.Id && !manager.IsForgotten(forgottenClient), "Catch a forgotten window into an explicitly chosen slot");
        Native.GetWindowRect(forgottenClient.Handle, out var caughtBounds);
        Check(caughtBounds.Rectangle == manager.Settings.Current.Slots[3].Bounds, "Caught window occupies the requested physical slot");
        manager.Rescan();
        Check(!manager.Accounts.ContainsKey(2) && manager.Accounts[4].Pid == replacement.Id, "Numbered old title does not override manual slot choice");
        manager.Assign(2, forgottenClient.Handle); Pump();
        Check(manager.Accounts[2].Pid == replacement.Id && !manager.Accounts.ContainsKey(4), "Reassign an existing client into an empty slot");
        var rejectedAssignment = false;
        try { manager.Assign(6, forgottenClient.Handle); } catch (ArgumentOutOfRangeException) { rejectedAssignment = true; }
        Check(rejectedAssignment && manager.Accounts[2].Pid == replacement.Id, "5-man rejects invalid target slots without changing assignments");
        for (var i = 4; i <= 5; i++) eligible.Add(StartClient(i).Id);
        manager.Rescan(); Pump();
        Check(manager.Accounts.Count == 5, "All five clients attached before rapid F2–F5 regression sequence");
        foreach (var client in manager.Accounts.Values) Native.SendMessage(client.Handle, TestClientForm.CounterMessage, 2, 0);
        using (var rapidKeys = new KeyboardHook(manager, n => { manager.Swap(n); manager.ObserveLayout(); }, () => { }, false))
            foreach (var key in new[] { Keys.F2, Keys.F3, Keys.F4, Keys.F5 })
            {
                var binding = new KeyBinding((int)key, false, false, false);
                Check(rapidKeys.HandleKey(binding, true, manager.Accounts[1].Handle), $"Rapid {key} routes with all five clients attached");
                rapidKeys.HandleKey(binding, false, 0);
            }
        Pump(200); context.AssertLayout(5);
        Check(manager.MainAccount == 5 && manager.Accounts.Values.All(w => Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 1, 0) == 0),
            "Rapid F2–F5 ends with account 5 main and no frame-style rewrites");
        manager.ResetLayout(); Pump();
        for (var i = 6; i <= 10; i++) eligible.Add(StartClient(i).Id);
        manager.SwitchProfile("10-man"); Pump();
        Check(manager.Accounts.Count == 10, "Switch to 10-man discovers all ten clients");
        var extra = StartClient(11); eligible.Add(extra.Id);
        var originalTen = manager.Accounts[10];
        using (var assignmentMenu = new ContextMenuStrip())
        {
            WindowMenus.AddTo(assignmentMenu, manager, action => action());
            var catcher = (ToolStripMenuItem)assignmentMenu.Items[0];
            Check(catcher.DropDownItems.OfType<ToolStripMenuItem>().Count(item => item.Text?.StartsWith("Slot ", StringComparison.Ordinal) == true) == 10, "Catch menu exposes all ten slots");
            var slotTen = (ToolStripMenuItem)catcher.DropDownItems[9];
            var extraWindow = manager.Discover().Single(w => w.Pid == extra.Id);
            var catchChoice = slotTen.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Tag is nint handle && handle == extraWindow.Handle);
            Check(catchChoice.Text == manager.Describe(extraWindow, newest: true).Replace("&", "&&", StringComparison.Ordinal) && catchChoice.Text!.StartsWith("Newest · Unassigned · PID", StringComparison.Ordinal),
                "Tray picker labels the newest unassigned client with the shared description");
            catchChoice.PerformClick(); Pump();
            Check(manager.Accounts[10].Pid == extra.Id && manager.IsForgotten(originalTen), "Tray catch replaces occupied slot 10 and excludes displaced client");
            manager.Forget(10); manager.Rescan();
            Check(!manager.Accounts.ContainsKey(10), "Forgotten replacement and displaced client both stay outside auto-capture");
            var forgetter = (ToolStripMenuItem)assignmentMenu.Items[1];
            forgetter.DropDownItems.OfType<ToolStripMenuItem>().First().PerformClick();
            Check(!manager.Accounts.ContainsKey(1), "Tray forget action releases its numbered account");
            manager.Assign(1, first);
            manager.Assign(10, originalTen.Handle); Pump();
            catchChoice = slotTen.DropDownItems.OfType<ToolStripMenuItem>().Single(item => item.Tag is nint handle && handle == extraWindow.Handle);
            extra.CloseMainWindow(); extra.WaitForExit(5000); Pump();
            var staleRejected = false;
            try { catchChoice.PerformClick(); } catch (InvalidOperationException) { staleRejected = true; }
            Check(staleRejected && manager.Accounts[10].Pid == originalTen.Pid, "Closed tray candidate is rejected without disturbing the current client");
        }
        manager.Swap(10); Pump(); context.AssertLayout(10);
        manager.ResetLayout(); manager.SaveCurrentPositions();
        Check(manager.Settings.Current.Slots[0].Width == 165, "Save current window positions reads native bounds");
        manager.Assign(2, manager.Accounts[3].Handle); Pump();
        Check(manager.Accounts[2].Pid == context.Three.Id && manager.Accounts[3].Pid == replacement.Id, "Manual account assignment exchanges identities");
        manager.SwitchProfile("5-man"); Pump();
        Check(!manager.IsManaged(manager.Accounts[10].Handle), "5-man excludes extra clients from mouse focus and hotkeys");
    }

/// <summary>Restart this client: close one client, reserve its account, adopt the next new client, touch nothing else.</summary>
    private static void RestartGroup(Context context)
    {
        var manager = context.Windows();
        if (manager.Settings.ActiveProfile != "5-man") { manager.SwitchProfile("5-man"); Pump(); }
        var eligible = context.Eligible;
        manager.Rescan();
        for (var n = 1; n <= 3; n++)
            if (!manager.Accounts.ContainsKey(n)) { eligible.Add(StartClient(n).Id); manager.Rescan(); }
        manager.ResetLayout(); Pump();
        var defaultClose = manager.CloseClient;
        void ResetCounters() { foreach (var w in manager.Accounts.Values) Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 2, 0); }
        bool Untouched(IEnumerable<ManagedWindow> windows) => windows.All(w => Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 0, 0) == 0 && Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 1, 0) == 0);
        Rectangle Bounds(ManagedWindow w) { Native.GetWindowRect(w.Handle, out var r); return r.Rectangle; }
        bool Refused(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }

        // Non-main restart; a client started before the restart (titled "WoW 2") must not take account 2.
        var decoy = StartClient(2);
        var old = manager.Accounts[2];
        var others = manager.Accounts.Where(p => p.Key != 2).Select(p => p.Value).ToList();
        ResetCounters();
        Check(Refused(() => manager.BeginRestart(2, others[0])) && manager.RestartingAccount == null && ReferenceEquals(manager.Accounts[2], old),
            "Restart refuses when the slot no longer holds the confirmed client");
        manager.BeginRestart(2, old);
        Check(manager.RestartingAccount == 2 && !manager.Accounts.ContainsKey(2) && !manager.Input.Managed.ContainsKey(old.Handle) && !manager.Swap(2),
            "Restart detaches only the chosen account and removes it from keys and hover");
        Check(Refused(() => manager.BeginRestart(3)) && Refused(manager.ResetLayout) && Refused(() => manager.Forget(3)) && Refused(() => manager.SwitchProfile("10-man"))
            && Refused(() => manager.Assign(4, others[0].Handle)) && manager.Settings.ActiveProfile == "5-man" && manager.RestartingAccount == 2,
            "A pending restart blocks other restarts, layout, forget, assign and profile changes without side effects");
        Process.GetProcessById(old.Pid).WaitForExit(5000); Pump(); manager.Rescan();
        Check(manager.RestartAwaitingClient && !manager.Accounts.Values.Any(w => w.Pid == old.Pid), "Restart sees the old client exit and waits for a new one");
        eligible.Add(decoy.Id); manager.Rescan(); Pump();
        Check(!manager.Accounts.ContainsKey(2) && manager.RestartAwaitingClient, "A client started before the restart never takes the reserved account");
        var decoyAccount = manager.Accounts.FirstOrDefault(p => p.Value.Pid == decoy.Id).Key;
        Check(decoyAccount == 0 || Bounds(manager.Accounts[decoyAccount]) == manager.Settings.Current.Slots[decoyAccount - 1].Bounds, "A client discovered during a restart is placed in its own slot");
        var replacement = StartClient(7); eligible.Add(replacement.Id); manager.Rescan(); Pump();
        Check(manager.RestartingAccount == null && manager.Accounts[2].Pid == replacement.Id && Bounds(manager.Accounts[2]) == manager.Settings.Current.Slots[1].Bounds,
            "The next new client takes the restarted account and its slot");
        Check(Untouched(others), "From close to adoption, including other discovery, no other client gets a position or style change");
        if (decoyAccount != 0) manager.Forget(decoyAccount);
        manager.ResetLayout(); Pump();

        // Restarting the main client keeps the main account and returns the new client to slot 1.
        manager.Swap(3); Pump(400);
        Check(manager.MainAccount == 3, "Account 3 is main before its restart");
        old = manager.Accounts[3];
        others = manager.Accounts.Values.Where(w => w != old).ToList();
        var otherBounds = others.ToDictionary(w => w.Handle, Bounds);
        ResetCounters();
        manager.BeginRestart(3);
        Process.GetProcessById(old.Pid).WaitForExit(5000); Pump(); manager.Rescan();
        Check(manager.MainAccount == 3 && !manager.Accounts.ContainsKey(3) && manager.RestartAwaitingClient, "Restarting the main client keeps it the main account");
        replacement = StartClient(8); eligible.Add(replacement.Id); manager.Rescan(); Pump();
        Check(manager.MainAccount == 3 && manager.Accounts[3].Pid == replacement.Id && Bounds(manager.Accounts[3]) == manager.Settings.Current.Slots[0].Bounds,
            "The restarted main client returns to the main slot");
        // Closing the focused client lets Windows activate another one (a Z-order message the kit does
        // not send), so here geometry and style writes are checked rather than every position message.
        Check(others.All(w => Bounds(w) == otherBounds[w.Handle] && Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 1, 0) == 0),
            "Restarting the main client moves and restyles no other client");
        manager.ResetLayout(); Pump();

        // A client that ignores the close request: offered once; Keep it restores the exact mapping.
        manager.CloseClient = static _ => true;
        manager.RestartForceAfterMs = 0;
        old = manager.Accounts[2];
        var everyone = manager.Accounts.Values.ToList();
        ResetCounters();
        manager.BeginRestart(2);
        Check(manager.ForceEndDue() == old.Pid && manager.ForceEndDue() == null, "A client that does not close is offered for ending once");
        manager.CancelRestart();
        Check(manager.RestartingAccount == null && ReferenceEquals(manager.Accounts[2], old) && manager.MainAccount == 1 && manager.Input.Managed.ContainsKey(old.Handle) && Untouched(everyone),
            "Keeping a client that did not close restores its account with no window changes");
        manager.RestartTimeoutMs = 0;
        manager.BeginRestart(2); manager.Rescan();
        Check(manager.RestartingAccount == null && ReferenceEquals(manager.Accounts[2], old) && manager.Status.Contains("expired", StringComparison.Ordinal) && manager.Status.Contains($"PID {old.Pid}", StringComparison.Ordinal),
            "An expired restart restores a still-running client to its account and says so");
        manager.RestartTimeoutMs = 10 * 60 * 1000;
        manager.BeginRestart(2);
        Check(manager.ForceEndDue() == old.Pid, "Force-end offered for the restart");
        using (var oldProcess = Process.GetProcessById(old.Pid))
        {
            manager.ForceEndRestart();
            Check(oldProcess.WaitForExit(5000), "End process ends only the restarting client");
        }
        manager.Rescan();
        Check(manager.RestartAwaitingClient && manager.Accounts.Values.All(w => w.Valid), "Other clients keep running after a forced end");
        manager.CancelRestart(); manager.Rescan();
        Check(manager.RestartingAccount == null && !manager.Accounts.ContainsKey(2) && manager.Status.Contains("Catch / assign", StringComparison.Ordinal), "Cancelling after the client exited leaves the slot empty and says what to do");
        Check(Refused(() => manager.BeginRestart(2)) && manager.RestartingAccount == null, "Restart refuses an empty slot without side effects");
        manager.CloseClient = defaultClose;
        manager.RestartForceAfterMs = 20000;
        eligible.Add(StartClient(2).Id); manager.Rescan(); Pump();
        Check(manager.Accounts.ContainsKey(2), "Normal discovery refills the slot after a cancelled restart");

        // Detection: repeated frame-restored drift is reported, and observing writes nothing.
        manager.ResetLayout(); Pump();
        var watched = manager.Accounts[3];
        void GameRestoresFrame(int width)
        {
            var style = (long)Native.GetWindowLongPtr(watched.Handle, Native.GwlStyle) | 0x00C00000;
            Native.SetWindowLongPtr(watched.Handle, Native.GwlStyle, (nint)style);
            var slot = manager.Settings.Current.Slots[2].Bounds;
            Native.Position(watched.Handle, new Rectangle(slot.X, slot.Y, width, slot.Height + 20), frameChanged: true);
            Pump();
            ResetCounters();
            manager.ObserveLayout();
        }
        GameRestoresFrame(200);
        Check(!manager.GraphicsResetSuspected(watched) && Untouched(manager.Accounts.Values), "One frame-restored observation is logged but not yet reported");
        ResetCounters(); manager.ObserveLayout();
        Check(manager.GraphicsResetSuspected(watched) && Untouched(manager.Accounts.Values), "Frame-restored drift seen in two samples, even unchanged, is reported as a suspected graphics reset, with no writes");
        manager.ResetLayout(); Pump();

        // Controls: confirmation gates the restart; the same button cancels it.
        manager.CloseClient = static _ => true;
        var answer = false;
        using (var panel = new ControlsForm(manager, action => action(), _ => { }, () => { }, () => { }, () => { }, () => { }, _ => answer))
        {
            panel.Show(); Pump();
            var rows = (DataGridView)panel.Controls.Find("controlSlots", true).Single();
            var button = (Button)panel.Controls.Find("controlRestart", true).Single();
            Check(rows.Rows[2].Cells[2].Value?.ToString()?.Contains("suspected graphics reset", StringComparison.Ordinal) == true, "Controls name the client with a suspected graphics reset");
            rows.CurrentCell = rows.Rows[1].Cells[0];
            button.PerformClick();
            Check(manager.RestartingAccount == null, "Declining the confirmation leaves the client alone");
            // The confirmation runs a nested message loop: the slot can change before Yes returns.
            var confirmed = manager.Accounts[2];
            var swappedIn = manager.Accounts[3];
            using (var swapped = new ControlsForm(manager, action => { try { action(); } catch (InvalidOperationException) { } }, _ => { }, () => { }, () => { }, () => { }, () => { },
                _ => { manager.Assign(2, swappedIn.Handle); return true; }))
            {
                swapped.Show(); Pump();
                var swappedRows = (DataGridView)swapped.Controls.Find("controlSlots", true).Single();
                swappedRows.CurrentCell = swappedRows.Rows[1].Cells[0];
                ((Button)swapped.Controls.Find("controlRestart", true).Single()).PerformClick();
            }
            Check(manager.RestartingAccount == null && confirmed.Valid && swappedIn.Valid && ReferenceEquals(manager.Accounts[2], swappedIn),
                "A slot changed during confirmation closes neither client");
            manager.Assign(2, confirmed.Handle); Pump();
            rows.CurrentCell = rows.Rows[1].Cells[0];
            answer = true; button.PerformClick();
            Check(manager.RestartingAccount == 2 && button.Text == "Cancel restart" && rows.Rows[1].Cells[2].Value?.ToString()?.StartsWith("Restarting", StringComparison.Ordinal) == true,
                "Confirmed Restart client starts the restart and shows it");
            button.PerformClick();
            Check(manager.RestartingAccount == null && manager.Accounts.ContainsKey(2) && button.Text == "Restart client", "Cancel restart returns the client to its slot");
        }
        manager.CloseClient = defaultClose;
    }

    private static void ControlsUiGroup(Context context)
    {
        var manager = context.Windows();
        if (manager.Settings.ActiveProfile != "5-man") { manager.SwitchProfile("5-man"); Pump(); }
        ControlsForm? panel = null;
        var exitClicks = 0;
        using (panel = new ControlsForm(manager, action => action(),
            name => { manager.SwitchProfile(name); panel?.RefreshState(); },
            () => { manager.Settings.MouseFocus = !manager.Settings.MouseFocus; panel?.RefreshState(); },
            () => { manager.Paused = !manager.Paused; panel?.RefreshState(); }, () => { }, () => exitClicks++))
        {
            panel.Show(); panel.RefreshWindows(); Pump();
            var rows = (DataGridView)panel.Controls.Find("controlSlots", true).Single();
            Check(rows.Rows.Count == 5 && !manager.Editing, "Compact controls show the live layout without pausing game management");
            rows.CurrentCell = rows.Rows[2].Cells[0];
            var assigned = manager.Accounts[3];
            ((Button)panel.Controls.Find("controlForget", true).Single()).PerformClick();
            Check(!manager.Accounts.ContainsKey(3) && manager.IsForgotten(assigned), "Control window Forget button releases the selected slot");
            var choices = (ComboBox)panel.Controls.Find("controlWindows", true).Single();
            var choice = choices.Items.Cast<object>().Single(c => c.ToString()!.Contains($"PID {assigned.Pid} ·", StringComparison.Ordinal));
            Check(choice.ToString()!.Contains("Forgotten · PID", StringComparison.Ordinal), "Control window lists a forgotten client with the shared description");
            choices.SelectedItem = choice;
            ((Button)panel.Controls.Find("controlCatch", true).Single()).PerformClick();
            Check(manager.Accounts[3].Handle == assigned.Handle, "Control window catches the chosen client back into the selected slot");
            var picker = (ComboBox)panel.Controls.Find("controlProfile", true).Single();
            picker.SelectedItem = "10-man";
            Check(rows.Rows.Count == 10 && manager.Settings.ActiveProfile == "10-man", "Control window profile picker updates the live layout");
            using var snapshot = new Bitmap(panel.Width, panel.Height);
            panel.DrawToBitmap(snapshot, new Rectangle(Point.Empty, panel.Size));
            snapshot.Save(Path.Combine(AppContext.BaseDirectory, "controls-preview.png"));
            var count = manager.Accounts.Count;
            panel.Close();
            Check(!panel.IsDisposed && !panel.Visible && manager.Accounts.Count == count, "Closing compact controls hides them without detaching clients");
            panel.Show(); Pump();
            Check(panel.Visible && !panel.IsDisposed, "Hidden controls can reopen while management keeps running");
            ((Button)panel.Controls.Find("controlExit", true).Single()).PerformClick();
            Check(exitClicks == 1, "Control window Exit button invokes app shutdown");
        }
        if (manager.Settings.ActiveProfile != "5-man") { manager.SwitchProfile("5-man"); Pump(); }
        AppSettings? edited = null;
        var displayName = manager.Settings.DisplayName;
        using (var form = new SettingsForm(manager, settings => { edited = settings; manager.Settings = settings; }))
        {
            form.Show(); Pump();
            Check(form.Text == displayName, "Settings window uses the configured display name");
            var hint = (Label)form.Controls.Find("hint", true).Single();
            Check(hint.ForeColor == DarkTheme.Muted, "Settings hint starts in the theme's muted colour");
            var names = (TextBox)form.Controls.Find("displayName", true).Single();
            names.Text = AppSettings.DefaultDisplayName;
            var profilePicker = (ComboBox)form.Controls.Find("profiles", true).Single();
            profilePicker.SelectedItem = "10-man"; Pump();
            var layout = (DataGridView)form.Controls.Find("layout", true).Single();
            Check(layout.Rows.Count == 10, "Layout editor switches to ten slots");
            // Drive the recorder through the grid's real key path.
            layout.CurrentCell = layout.Rows[0].Cells["Hotkey"];
            var press = typeof(HotkeyGrid).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var keyMessage = new Message();
            var slotOneKey = (string?)layout.Rows[0].Cells["Hotkey"].Value;
            var slotTwoKey = (string?)layout.Rows[1].Cells["Hotkey"].Value;
            Check((bool)press.Invoke(layout, [keyMessage, ToKeys(KeyBinding.Parse(slotTwoKey!))])! && (string?)layout.Rows[0].Cells["Hotkey"].Value == slotTwoKey && (string?)layout.Rows[1].Cells["Hotkey"].Value == slotOneKey,
                "Pressing another slot's key records it and swaps the two keys");
            layout.CurrentCell = layout.Rows[2].Cells["Hotkey"];
            press.Invoke(layout, [keyMessage, Keys.Control | Keys.D3]);
            Check((string?)layout.Rows[2].Cells["Hotkey"].Value == "Ctrl+3", "Ctrl+digit recorded as a swap key");
            layout.CurrentCell = layout.Rows[3].Cells["Hotkey"];
            press.Invoke(layout, [keyMessage, Keys.Alt | Keys.D1]);
            Check((string?)layout.Rows[3].Cells["Hotkey"].Value == "Alt+1", "Alt+1 recorded as a swap key");
            press.Invoke(layout, [keyMessage, Keys.Control | Keys.Shift | Keys.F4]);
            Check((string?)layout.Rows[3].Cells["Hotkey"].Value == "Ctrl+Shift+F4", "Multi-modifier combination recorded");
            Check(hint.ForeColor == DarkTheme.Muted, "Settings hint keeps the muted colour after recording a key");
            press.Invoke(layout, [keyMessage, Keys.Alt | Keys.D1]);
            layout.CurrentCell = layout.Rows[2].Cells["Hotkey"];
            press.Invoke(layout, [keyMessage, Keys.Q]);
            Check((string?)layout.Rows[2].Cells["Hotkey"].Value == "Ctrl+3" && hint.Text.Contains("need Ctrl or Alt", StringComparison.Ordinal) && hint.ForeColor == DarkTheme.Warning, "Plain letter refused with an explanation");
            Check(!(bool)press.Invoke(layout, [keyMessage, Keys.Down])!, "Arrow keys still navigate the grid");
            var toggleBox = (HotkeyBox)form.Controls.Find("toggleKey", true).Single();
            var toggleBefore = toggleBox.Text;
            Check(toggleBox.HotkeyPressed!(KeyBinding.Parse(slotOneKey!)) && toggleBox.Text == toggleBefore, "Toggle key refuses a swap key");
            toggleBox.HotkeyPressed!(KeyBinding.Parse("NumPad0"));
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-preview.png"));
            ((Button)form.Controls.Find("save", true).Single()).PerformClick(); Pump();
            Check(edited?.DisplayName == AppSettings.DefaultDisplayName && edited.ActiveProfile == "10-man", "Settings editor saves custom name and selected profile");
            Check(edited!.MouseToggleHotkey == "NumPad0" && edited.Current.Slots[0].Hotkey == slotTwoKey && edited.Current.Slots[2].Hotkey == "Ctrl+3" && edited.Current.Slots[3].Hotkey == "Alt+1" && manager.Input.Toggle == KeyBinding.Parse("NumPad0"),
                "Recorded keys save and reach the hook snapshot");
            // The live settings get a copy: the form's working draft never becomes the live object.
            var draft = typeof(SettingsForm).GetField("draft", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form);
            Check(draft != null && !ReferenceEquals(draft, edited) && !ReferenceEquals(draft, manager.Settings), "Saved settings are not the form's working copy");
        }
    }

    private static void HooksGroup(Context context)
    {
        var manager = context.Windows();
        var liveApp = context.LiveApp;
        // Install the actual low-level hook too; key routing above is exercised without synthetic input.
        using (var actualHook = new KeyboardHook(manager, _ => { }, () => { }))
            Check(true, "Native keyboard hook installs and disposes");
        {
            // Production runs MouseTick on the focus worker; a wake must produce a decision without UI pumping.
            var stationary = new Native.Point(-30000, -30000);
            manager.ReadPointer = () => stationary;
            manager.Swap(1);
            using var worker = new FocusWorker(manager.MouseTick, 1000);
            stationary.X++; worker.Wake();
            var deadline = Environment.TickCount64 + 1000;
            while (manager.MouseDecision != "Outside" && Environment.TickCount64 < deadline) Thread.Sleep(5);
            Check(manager.MouseDecision == "Outside", "Focus worker evaluates hover on wake, off the UI thread");
            manager.ReadPointer = context.RealPointer;
        }
        var threadedEvents = 0;
        using (var hookThread = new HookThread(installed => installed.Add(new MouseHook(_ => Interlocked.Increment(ref threadedEvents)))))
        {
            Check(true, "Input hooks install on their own thread");
            if (!liveApp)
            {
                // Block this (UI) thread completely: the hook thread must still receive input.
                var delta = 3;
                for (var attempt = 0; attempt < 30 && Volatile.Read(ref threadedEvents) == 0; attempt++)
                {
                    MoveMouseBy(delta);
                    delta = -delta;
                    Thread.Sleep(20);
                }
                Check(Volatile.Read(ref threadedEvents) > 0, "Hook thread receives mouse input while the UI thread is blocked");
            }
            else results.Add("SKIP: Synthetic pointer movement for the hook thread while user's app is running.");
        }
        var mouseEvents = 0;
        using (var mouseHook = new MouseHook(_ => mouseEvents++))
        {
            Check(true, "Native mouse hook installs and disposes");
            if (!liveApp)
            {
                var delta = 3;
                var attempts = 0;
                // Exercise the hook under the real message loop used in production.
                // SetCursorPos is not a reliable low-level input event source. This
                // test-only movement sends no button/key input and restores the pointer.
                using var hookLoop = new ApplicationContext();
                using var pulse = new System.Windows.Forms.Timer { Interval = 20 };
                pulse.Tick += (_, _) =>
                {
                    if (mouseEvents > 0 || attempts++ >= 30) { hookLoop.ExitThread(); return; }
                    MoveMouseBy(delta);
                    delta = -delta;
                };
                pulse.Start(); Application.Run(hookLoop); pulse.Stop();
                Check(mouseEvents > 0, "Native mouse movement hook delivers events without polling");
            }
            else results.Add("SKIP: Synthetic pointer movement while user's app is running.");
        }
    }

    private static void LiveTrackingGroup(Context context)
    {
        var manager = context.Windows();
        if (context.LiveApp || !manager.Accounts.TryGetValue(1, out var tracked) || !tracked.Valid)
        {
            results.Add("SKIP: Live Windows tracking test while user's app is running.");
            return;
        }
        // End to end with the real system setting: Windows, not the app, activates the hovered client.
        var activateBefore = manager.ActivateWindow; var appActivations = 0;
        manager.ActivateWindow = _ => { appActivations++; return false; };
        var working = context.Working;
        using var other = new Form { Text = "Foreground placeholder", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(working.Right - 260, working.Bottom - 200, 200, 150), ShowInTaskbar = false };
        other.Show(); Native.Activate(other.Handle); Pump();
        Expose(tracked.Handle);
        Native.GetWindowRect(tracked.Handle, out var trackedRect);
        // Start outside the client, as a real sweep from another window does.
        Native.SetCursorPos(trackedRect.Left - 40, trackedRect.Top + 30); Pump();
        var marker = Path.Combine(context.Temporary, "live-tracking.restore");
        using (var system = new WindowTracking(marker))
        {
            Check(system.Unavailable == null, $"Live tracking test can record the user's setting ({system.Unavailable})");
            var before = system.Original;
            manager.SystemTracking = system.Set;
            using (new HookThread(installed => installed.Add(new MouseHook(manager.PointerMoved))))
            {
                for (var attempt = 0; attempt < 40 && Native.GetForegroundWindow() != tracked.Handle; attempt++)
                {
                    Native.GetCursorPos(out var at);
                    MoveMouseBy(at.X < trackedRect.Left + 40 ? 4 : (attempt % 2 == 0 ? 2 : -2));
                    Pump(25);
                }
                Native.GetCursorPos(out var cursorNow);
                Check(Native.GetForegroundWindow() == tracked.Handle && appActivations == 0, $"Windows' active window tracking focuses the hovered client by itself (tracking={system.Enabled}; input={manager.Input.MouseFocus}/{manager.Input.Paused}/{manager.Input.Editing}; hover={manager.HoverTarget(cursorNow)}; target={tracked.Handle}; managed={manager.IsManaged(tracked.Handle)}; foreground={Native.Title(Native.GetForegroundWindow())}; appActivations={appActivations}; original={system.Original})");
                // Regression: a keyboard swap under the stationary pointer must keep the chosen account
                // focused; Windows' re-check of the window under the pointer must not hand focus back.
                manager.ActivateWindow = activateBefore;
                var swapTo = manager.Accounts.Where(p => p.Key != 1 && p.Key <= manager.Settings.Current.Slots.Count && p.Value.Valid).Select(p => p.Key).First();
                // Production route: the swap's activation runs on the focus worker.
                using (var worker = new FocusWorker(manager.MouseTick, 50))
                {
                    manager.RequestActivation = worker.Run;
                    manager.Swap(swapTo); Pump(700);
                    Check(Native.GetForegroundWindow() == manager.Accounts[swapTo].Handle,
                        $"Keyboard swap keeps the chosen account focused with Windows tracking on (foreground={Native.Title(Native.GetForegroundWindow())}; expected=account {swapTo})");
                    manager.Swap(1); Pump(300);
                    manager.RequestActivation = null;
                }
            }
            manager.SystemTracking = null;
            system.Dispose();
            using var after = new WindowTracking(marker);
            Check(after.Original == before, "Live tracking test restores the user's system setting");
        }
        Check(!File.Exists(marker), "Live tracking test removes its marker");
        manager.ActivateWindow = activateBefore;
        Unexpose(tracked.Handle);
    }

    private static void RestoreGroup(Context context)
    {
        results.Add("GROUP: restore");
        var first = context.First;
        context.Dispose();
        Pump();
        Check(Native.GetWindowLongPtr(first, Native.GwlStyle) == context.OriginalStyle, "Exit restores original window style");
        var restored = new Native.Placement { Length = Marshal.SizeOf<Native.Placement>() };
        Native.GetWindowPlacement(first, ref restored);
        Check(restored.Normal.Rectangle == context.InitialPlacement.Normal.Rectangle, "Exit restores original window placement");
    }
}

internal sealed class TestClientForm : Form
{
    internal const uint CounterMessage = 0x8141;
    private int positionChanges, styleChanges;
    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == CounterMessage)
        {
            if ((int)message.WParam == 2) { positionChanges = styleChanges = 0; message.Result = 0; }
            else message.Result = (int)message.WParam == 0 ? positionChanges : styleChanges;
            return;
        }
        if ((uint)message.Msg == Native.WmWindowPosChanged) positionChanges++;
        if ((uint)message.Msg == Native.WmStyleChanged) styleChanges++;
        base.WndProc(ref message);
    }
}
