using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace WoWWindowKit;

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
    private static readonly List<string> results = [];
    private static readonly List<Process> children = [];
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        results.Add("PASS: " + name);
    }
    private static void Pump(int milliseconds = 80)
    {
        var stop = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < stop) { Application.DoEvents(); Thread.Sleep(10); }
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
    internal static int Run()
    {
        var liveApp = Mutex.TryOpenExisting(@"Local\WoWWindowKit.1C825122", out var liveMutex);
        liveMutex?.Dispose();
        WindowManager? manager = null;
        var report = Path.Combine(AppContext.BaseDirectory, "self-test-results.txt");
        var temporary = Path.Combine(Path.GetTempPath(), "WoWWindowKit-test-" + Guid.NewGuid().ToString("N"));
        Native.GetCursorPos(out var originalCursor);
        Directory.CreateDirectory(temporary);
        try
        {
            var imported = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.default.json"));
            Check(imported.Profiles.Count == 2 && imported.Profiles[0].Slots.Count == 5 && imported.Profiles[1].Slots.Count == 10, "Both imported profile sizes");
            Check(imported.Profiles[0].Slots[0].Bounds == new Rectangle(0, 0, 1880, 1392) && imported.Profiles[1].Slots[9].Bounds == new Rectangle(2048, 1152, 512, 288), "Imported 5-man and 10-man geometry");
            Check(imported.Profiles[1].Slots.Select(s => s.Hotkey).SequenceEqual(Enumerable.Range(1, 10).Select(n => "F" + n)), "10-man F1–F10 bindings");
            var settingsPath = Path.Combine(temporary, "settings.json");
            imported.Save(settingsPath); imported.DisplayName = "A different name"; imported.Save(settingsPath);
            Check(AppSettings.Load(settingsPath).DisplayName == "A different name" && AppSettings.Load(settingsPath + ".bak").DisplayName == "Notepad++", "Custom app name, atomic save and previous-settings backup");
            Check(!File.ReadAllText(settingsPath).Contains("\"Current\"") && !File.ReadAllText(settingsPath).Contains("\"Bounds\""), "Only user-editable settings serialized");
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
            // Small real native windows, in separate processes, exercise HWND and foreground boundaries.
            var working = Screen.PrimaryScreen!.WorkingArea;
            foreach (var profile in imported.Profiles)
                for (var i = 0; i < profile.Slots.Count; i++)
                {
                    var slot = profile.Slots[i]; slot.X = working.X + 30 + (i % 5) * 170; slot.Y = working.Y + 30 + (i / 5) * 170;
                    slot.Width = i == 0 ? 165 : 150; slot.Height = 150;
                }
            var one = StartClient(1); var two = StartClient(2); var three = StartClient(3);
            var eligible = new HashSet<int> { one.Id, two.Id, three.Id };
            manager = new WindowManager(imported, pid => eligible.Contains(pid));
            // Live user button state must not make the mock-window assertions nondeterministic.
            manager.MouseInputHeld = static () => false;
            // Do not steal focus or move the pointer during the user's live session.
            if (liveApp) manager.ActivateWindow = static _ => true;
            manager.Rescan(); Pump();
            Check(manager.Accounts.Count == 3 && manager.Accounts[2].Pid == two.Id, "Separate-process discovery and existing numbered titles");
            var originalHandle = manager.Accounts[2].Handle;
            var initialPlacement = manager.Accounts[1].OriginalPlacement;
            var originalStyle = manager.Accounts[1].OriginalStyle;
            void AssertLayout(int main)
            {
                foreach (var pair in manager.Accounts.Where(p => p.Key <= manager.Settings.Current.Slots.Count))
                {
                    Native.GetWindowRect(pair.Value.Handle, out var actual);
                    var slot = pair.Key == main ? 1 : pair.Key == 1 ? main : pair.Key;
                    Check(actual.Rectangle == manager.Settings.Current.Slots[slot - 1].Bounds, $"Main {main}: account {pair.Key} occupies slot {slot}");
                }
            }
            AssertLayout(1);
            Check(((long)Native.GetWindowLongPtr(manager.Accounts[1].Handle, Native.GwlStyle) & Native.BorderMask) == 0, "Borderless game window style applied");
            foreach (var main in new[] { 2, 3, 2, 1 }) { manager.Swap(main); Pump(); AssertLayout(main); }
            Check(manager.Accounts[2].Handle == originalHandle, "F-key account identity survives repeated swaps");
            // These counters live in the separate mock-client processes, not the manager.
            foreach (var client in manager.Accounts.Values) Native.SendMessage(client.Handle, TestClientForm.CounterMessage, 2, 0);
            manager.Swap(2); Pump();
            Check(Native.SendMessage(manager.Accounts[3].Handle, TestClientForm.CounterMessage, 0, 0) == 0, "Swap leaves unrelated client's native position untouched");
            Check(manager.Accounts.Values.All(w => Native.SendMessage(w.Handle, TestClientForm.CounterMessage, 1, 0) == 0), "Swaps do not rewrite any client's window style");
            foreach (var main in new[] { 3, 1, 2, 3, 2, 1 }) manager.Swap(main);
            Pump(200); AssertLayout(1);
            Check(manager.MainAccount == 1, "Rapid async swaps preserve the final logical layout");
            {
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
                if (!liveApp)
                {
                    Native.ShowWindow(drifting, 3); Pump(); // maximized by the game
                    AssertObservationDoesNotWrite("Polling leaves a game-maximized window alone");
                    Native.ShowWindow(drifting, 4); Pump();
                }
                manager.ResetLayout(); Pump(); AssertLayout(1);
                Check(((long)Native.GetWindowLongPtr(drifting, Native.GwlStyle) & Native.BorderMask) == 0,
                    "Explicit Reapply layout still restores borderless slot placement");
                foreach (var main in new[] { 2, 3, 2, 3, 1 }) { manager.Swap(main); manager.ObserveLayout(); }
                Pump(200); AssertLayout(1);
                Check(manager.MainAccount == 1, "Rapid swaps with interleaved observation preserve the final layout");
            }
            Check(!manager.Swap(5), "Missing account swap does nothing");
            manager.Paused = true; Check(!manager.Swap(2), "Pause blocks swaps"); manager.Paused = false;
            var swapCalled = 0; var toggled = 0;
            using (var router = new KeyboardHook(manager, n => swapCalled = n, () => toggled++, false))
            {
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
                manager.Settings = imported;
            }
            var scanTimer = Stopwatch.StartNew();
            for (var i = 0; i < 10; i++) manager.Rescan();
            results.Add($"INFO: Rescan averages {scanTimer.Elapsed.TotalMilliseconds / 10:F2} ms with cached process identity.");
            Check(manager.Accounts.Count == 3 && manager.Accounts[2].Handle == originalHandle, "Cached process identity keeps assignments across scans");
            var first = manager.Accounts[1].Handle;
            var point = new Native.Point(imported.Current.Slots[0].X + 20, imported.Current.Slots[0].Y + 20);
            Native.SetWindowPos(first, -1, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2); Pump();
            Check(manager.HoverTarget(point) == first, "Exposed managed window qualifies for hover focus");
            var realPointer = manager.ReadPointer;
            var simulatedPointer = point;
            manager.ReadPointer = () => simulatedPointer;
            {
                // Windows' tracking is gated in the mouse hook; a fake records what the system would get.
                var requested = new List<bool>();
                manager.SystemTracking = requested.Add;
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
                manager.PointerMoved(point);
                Check(requested.SequenceEqual([false]), "Keyboard swap holds Windows tracking off under a stationary pointer");
                manager.PointerMoved(point with { X = point.X + 1 });
                Check(requested.SequenceEqual([false, true]), "First real mouse movement after a swap re-enables Windows tracking");
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
                manager.SystemTracking = null; manager.ActivateWindow = activate; manager.TrackingGraceMs = 150;
            }
            {
                var markerPath = Path.Combine(temporary, "tracking.restore");
                var system = new WindowTracking.Values(0, 1, 200);
                var applied = 0;
                var crashed = new WindowTracking(markerPath, () => system, v => { system = v; applied++; });
                crashed.Set(true); crashed.Set(true);
                Check(system == new WindowTracking.Values(1, 0, 0) && applied == 1 && File.Exists(markerPath), "Enabling tracking applies instant, no-raise values once and records the originals");
                crashed.Set(false);
                Check(system == new WindowTracking.Values(0, 1, 200), "Disabling restores the user's original tracking values");
                crashed.Set(true); // simulate a crash: never disposed
                var recovered = new WindowTracking(markerPath, () => system, v => system = v);
                Check(system == new WindowTracking.Values(0, 1, 200) && recovered.Original == system && !File.Exists(markerPath), "Next start repairs tracking left on by a crash");
                recovered.Set(true); recovered.Dispose();
                Check(system == new WindowTracking.Values(0, 1, 200) && !File.Exists(markerPath) && !recovered.Enabled, "Exit restores tracking and removes the marker");
                recovered.Set(true);
                Check(system == new WindowTracking.Values(0, 1, 200), "Tracking stays off after exit");
            }
            manager.Swap(2); Pump(); manager.MouseTick();
            Check(manager.MouseDecision == "Stationary after swap", "Stationary pointer does not undo a keyboard swap");
            var movement = Stopwatch.StartNew();
            simulatedPointer.X++; manager.MouseTick();
            Check(manager.MouseDecision != "Stationary after swap" && manager.MouseDecision != "Paused", "First mouse movement resumes hover without a 350 ms delay");
            results.Add($"INFO: First movement handled after {movement.Elapsed.TotalMilliseconds:F2} ms (mock windows).");
            manager.ResetLayout(); Pump();
            Native.SetWindowPos(first, -2, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
            using (var blocker = new Form { Text = "Non-game overlap", StartPosition = FormStartPosition.Manual, Bounds = imported.Current.Slots[0].Bounds, TopMost = true, ShowInTaskbar = false })
            {
                blocker.Show(); blocker.Activate();
                Native.SetWindowPos(blocker.Handle, -1, point.X - 20, point.Y - 20, 165, 150, 0); Pump();
                Check(manager.HoverTarget(point) == 0, "Overlapping non-game window blocks focus even inside WoW rectangle");
                simulatedPointer = point;
                if (!liveApp) Native.SetCursorPos(point.X, point.Y);
                Pump(100);
                var foreground = Native.GetForegroundWindow();
                manager.MouseTick();
                if (!liveApp) Check(Native.GetForegroundWindow() == foreground, "Mouse focus does not steal focus outside WoW");
                else results.Add("SKIP: Foreground assertions while the user's app is running; simulated focus does not interrupt live clients.");
                Native.SetWindowPos(blocker.Handle, -1, working.Right - 250, working.Bottom - 180, 165, 150, 0);
                Native.SetWindowPos(first, -1, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
                Pump(400);
                var focusDeadline = Environment.TickCount64 + 1500;
                do { manager.MouseTick(); Pump(50); } while (!liveApp && Native.GetForegroundWindow() != first && Environment.TickCount64 < focusDeadline);
                Native.GetCursorPos(out var observed);
                if (!liveApp && Native.GetForegroundWindow() != first)
                    throw new InvalidOperationException($"Focus resume failed: {manager.MouseDecision}; expected={first}; hover={manager.HoverTarget(observed)}; {manager.Status}; foreground={Native.Title(Native.GetForegroundWindow())}");
                if (!liveApp) Check(true, "Returning pointer to exposed WoW resumes actual cross-process focus");
                var second = manager.Accounts[2].Handle;
                Native.SetWindowPos(second, -1, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
                var secondSlot = imported.Current.Slots[1];
                simulatedPointer = new Native.Point(secondSlot.X + 20, secondSlot.Y + 20);
                if (!liveApp) Native.SetCursorPos(secondSlot.X + 20, secondSlot.Y + 20);
                Pump();
                focusDeadline = Environment.TickCount64 + 1500;
                do { manager.MouseTick(); Pump(50); } while (!liveApp && Native.GetForegroundWindow() != second && Environment.TickCount64 < focusDeadline);
                if (!liveApp) Check(Native.GetForegroundWindow() == second, "Mouse hover transfers actual focus between client processes");
                Native.SetWindowPos(second, -2, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
            }
            Native.SetWindowPos(first, -2, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
            two.CloseMainWindow(); two.WaitForExit(5000); Pump(); manager.Rescan();
            Check(!manager.Accounts.ContainsKey(2) && manager.Accounts[3].Pid == three.Id, "Client exit leaves a stable empty account slot");
            var replacement = StartClient(2); eligible.Add(replacement.Id); manager.Rescan();
            Check(manager.Accounts[2].Pid == replacement.Id, "Restarted client reclaims the empty account number");
            var forgottenClient = manager.Accounts[2];
            var beforeForgetPlacement = forgottenClient.OriginalPlacement;
            Check(manager.Forget(2), "Forget releases the chosen account");
            Check(!manager.IsManaged(forgottenClient.Handle) && !manager.Input.SwapKeys.Values.Any(v => v.Handle == forgottenClient.Handle), "Forget removes the window from the hook snapshot immediately");
            Pump();
            var releasedPlacement = new Native.Placement { Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.Placement>() };
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
            Pump(200); AssertLayout(5);
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
            manager.Swap(10); Pump(); AssertLayout(10);
            manager.ResetLayout(); manager.SaveCurrentPositions();
            Check(manager.Settings.Current.Slots[0].Width == 165, "Save current window positions reads native bounds");
            manager.Assign(2, manager.Accounts[3].Handle); Pump();
            Check(manager.Accounts[2].Pid == three.Id && manager.Accounts[3].Pid == replacement.Id, "Manual account assignment exchanges identities");
            manager.SwitchProfile("5-man"); Pump();
            Check(!manager.IsManaged(manager.Accounts[10].Handle), "5-man excludes extra clients from mouse focus and hotkeys");
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
                choices.SelectedItem = choices.Items.Cast<object>().Single(c => c.ToString()!.Contains($"PID {assigned.Pid} ·", StringComparison.Ordinal));
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
            AppSettings? edited = null;
            using (var form = new SettingsForm(manager, settings => { edited = settings; manager.Settings = settings; }))
            {
                form.Show(); Pump();
                Check(form.Text == "A different name", "Settings window uses the configured display name");
                var names = (TextBox)form.Controls.Find("displayName", true).Single();
                names.Text = "Notepad++";
                var profilePicker = (ComboBox)form.Controls.Find("profiles", true).Single();
                profilePicker.SelectedItem = "10-man"; Pump();
                var layout = (DataGridView)form.Controls.Find("layout", true).Single();
                Check(layout.Rows.Count == 10, "Layout editor switches to ten slots");
                // Drive the recorder through the grid's real key path.
                layout.CurrentCell = layout.Rows[0].Cells["Hotkey"];
                var press = typeof(HotkeyGrid).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var keyMessage = new Message();
                Check((bool)press.Invoke(layout, [keyMessage, Keys.F2])! && (string?)layout.Rows[0].Cells["Hotkey"].Value == "F2" && (string?)layout.Rows[1].Cells["Hotkey"].Value == "F1",
                    "Pressing another slot's key records it and swaps the two keys");
                layout.CurrentCell = layout.Rows[2].Cells["Hotkey"];
                press.Invoke(layout, [keyMessage, Keys.Control | Keys.D3]);
                Check((string?)layout.Rows[2].Cells["Hotkey"].Value == "Ctrl+3", "Ctrl+digit recorded as a swap key");
                layout.CurrentCell = layout.Rows[3].Cells["Hotkey"];
                press.Invoke(layout, [keyMessage, Keys.Alt | Keys.D1]);
                Check((string?)layout.Rows[3].Cells["Hotkey"].Value == "Alt+1", "Alt+1 recorded as a swap key");
                press.Invoke(layout, [keyMessage, Keys.Control | Keys.Shift | Keys.F4]);
                Check((string?)layout.Rows[3].Cells["Hotkey"].Value == "Ctrl+Shift+F4", "Multi-modifier combination recorded");
                press.Invoke(layout, [keyMessage, Keys.Alt | Keys.D1]);
                layout.CurrentCell = layout.Rows[2].Cells["Hotkey"];
                press.Invoke(layout, [keyMessage, Keys.Q]);
                Check((string?)layout.Rows[2].Cells["Hotkey"].Value == "Ctrl+3" && ((Label)form.Controls.Find("hint", true).Single()).Text.Contains("need Ctrl or Alt"), "Plain letter refused with an explanation");
                Check(!(bool)press.Invoke(layout, [keyMessage, Keys.Down])!, "Arrow keys still navigate the grid");
                var toggleBox = (HotkeyBox)form.Controls.Find("toggleKey", true).Single();
                Check(toggleBox.HotkeyPressed!(KeyBinding.Parse("F1")) && toggleBox.Text == "Ctrl+Shift+F", "Toggle key refuses a swap key");
                toggleBox.HotkeyPressed!(KeyBinding.Parse("NumPad0"));
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-preview.png"));
                ((Button)form.Controls.Find("save", true).Single()).PerformClick(); Pump();
                Check(edited?.DisplayName == "Notepad++" && edited.ActiveProfile == "10-man", "Settings editor saves custom name and selected profile");
                Check(edited!.MouseToggleHotkey == "NumPad0" && edited.Current.Slots[0].Hotkey == "F2" && edited.Current.Slots[2].Hotkey == "Ctrl+3" && edited.Current.Slots[3].Hotkey == "Alt+1" && manager.Input.Toggle == KeyBinding.Parse("NumPad0"),
                    "Recorded keys save and reach the hook snapshot");
            }
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
                manager.ReadPointer = realPointer;
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
                        var input = new TestMouseInput { Type = 0, X = delta, Flags = 1 };
                        SendInput(1, ref input, Marshal.SizeOf<TestMouseInput>());
                        delta = -delta;
                        Thread.Sleep(20);
                    }
                    Check(Volatile.Read(ref threadedEvents) > 0, "Hook thread receives mouse input while the UI thread is blocked");
                }
                else results.Add("SKIP: Synthetic pointer movement for the hook thread while user's app is running.");
            }
            if (!liveApp && manager.Accounts.TryGetValue(1, out var tracked) && tracked.Valid)
            {
                // End to end with the real system setting: Windows, not the app, activates the hovered client.
                var activateBefore = manager.ActivateWindow; var appActivations = 0;
                manager.ActivateWindow = _ => { appActivations++; return false; };
                using var other = new Form { Text = "Foreground placeholder", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(working.Right - 260, working.Bottom - 200, 200, 150), ShowInTaskbar = false };
                other.Show(); Native.Activate(other.Handle); Pump();
                Native.SetWindowPos(tracked.Handle, -1, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
                Native.GetWindowRect(tracked.Handle, out var trackedRect);
                // Start outside the client, as a real sweep from another window does.
                Native.SetCursorPos(trackedRect.Left - 40, trackedRect.Top + 30); Pump();
                var marker = Path.Combine(temporary, "live-tracking.restore");
                using (var system = new WindowTracking(marker))
                {
                    var before = system.Original;
                    manager.SystemTracking = system.Set;
                    using (new HookThread(installed => installed.Add(new MouseHook(manager.PointerMoved))))
                    {
                        for (var attempt = 0; attempt < 40 && Native.GetForegroundWindow() != tracked.Handle; attempt++)
                        {
                            Native.GetCursorPos(out var at);
                            var input = new TestMouseInput { Type = 0, X = at.X < trackedRect.Left + 40 ? 4 : (attempt % 2 == 0 ? 2 : -2), Flags = 1 };
                            SendInput(1, ref input, Marshal.SizeOf<TestMouseInput>());
                            Pump(25);
                        }
                        Native.GetCursorPos(out var cursorNow);
                        Check(Native.GetForegroundWindow() == tracked.Handle && appActivations == 0, $"Windows' active window tracking focuses the hovered client by itself (tracking={system.Enabled}; input={manager.Input.MouseFocus}/{manager.Input.Paused}/{manager.Input.Editing}; hover={manager.HoverTarget(cursorNow)}; target={tracked.Handle}; managed={manager.IsManaged(tracked.Handle)}; foreground={Native.Title(Native.GetForegroundWindow())}; appActivations={appActivations}; original={system.Original})");
                        // Regression: a keyboard swap under the stationary pointer must keep the chosen account
                        // focused; Windows' re-check of the window under the pointer must not hand focus back.
                        manager.ActivateWindow = activateBefore;
                        var swapTo = manager.Accounts.Where(p => p.Key != 1 && p.Key <= manager.Settings.Current.Slots.Count && p.Value.Valid).Select(p => p.Key).First();
                        manager.Swap(swapTo); Pump(700);
                        Check(Native.GetForegroundWindow() == manager.Accounts[swapTo].Handle,
                            $"Keyboard swap keeps the chosen account focused with Windows tracking on (foreground={Native.Title(Native.GetForegroundWindow())}; expected=account {swapTo})");
                        manager.Swap(1); Pump(300);
                    }
                    manager.SystemTracking = null;
                    system.Dispose();
                    Check(!File.Exists(marker) && new WindowTracking(marker).Original == before, "Live tracking test restores the user's system setting");
                }
                manager.ActivateWindow = activateBefore;
                Native.SetWindowPos(tracked.Handle, -2, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2);
            }
            else results.Add("SKIP: Live Windows tracking test while user's app is running.");
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
                        var input = new TestMouseInput { Type = 0, X = delta, Flags = 1 };
                        SendInput(1, ref input, Marshal.SizeOf<TestMouseInput>());
                        delta = -delta;
                    };
                    pulse.Start(); Application.Run(hookLoop); pulse.Stop();
                    Check(mouseEvents > 0, "Native mouse movement hook delivers events without polling");
                }
                else results.Add("SKIP: Synthetic pointer movement while user's app is running.");
            }
            manager.Dispose(); manager = null; Pump();
            Check(Native.GetWindowLongPtr(first, Native.GwlStyle) == originalStyle, "Exit restores original window style");
            var restored = new Native.Placement { Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.Placement>() };
            Native.GetWindowPlacement(first, ref restored);
            Check(restored.Normal.Rectangle == initialPlacement.Normal.Rectangle, "Exit restores original window placement");
            results.Add($"SUCCESS: {results.Count(r => r.StartsWith("PASS:"))} checks passed; {results.Count(r => r.StartsWith("SKIP:"))} test groups skipped.");
            File.WriteAllLines(report, results); return 0;
        }
        catch (Exception ex) { results.Add(ex.ToString()); File.WriteAllLines(report, results); return 1; }
        finally
        {
            if (!liveApp) Native.SetCursorPos(originalCursor.X, originalCursor.Y);
            manager?.Dispose();
            foreach (var child in children)
            {
                try { if (!child.HasExited) { child.CloseMainWindow(); if (!child.WaitForExit(1500)) child.Kill(); } } catch { }
                child.Dispose();
            }
            // Only the explicitly created test scratch folder is removed; keep the results report.
            var resolvedScratch = Path.GetFullPath(temporary);
            var scratchRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WoWWindowKit-test-"));
            if (resolvedScratch.StartsWith(scratchRoot, StringComparison.OrdinalIgnoreCase) && resolvedScratch.Length == scratchRoot.Length + 32)
                Directory.Delete(resolvedScratch, true);
        }
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
        if (message.Msg == 0x47) positionChanges++;
        if (message.Msg == 0x7D) styleChanges++;
        base.WndProc(ref message);
    }
}
