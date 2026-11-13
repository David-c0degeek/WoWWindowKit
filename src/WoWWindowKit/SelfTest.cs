using System.Diagnostics;
using System.Text.Json;

namespace WoWWindowKit;

internal static class SelfTest
{
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
            }
            var first = manager.Accounts[1].Handle;
            var point = new Native.Point(imported.Current.Slots[0].X + 20, imported.Current.Slots[0].Y + 20);
            Native.SetWindowPos(first, -1, 0, 0, 0, 0, Native.NoActivate | 0x1 | 0x2); Pump();
            Check(manager.HoverTarget(point) == first, "Exposed managed window qualifies for hover focus");
            var realPointer = manager.ReadPointer;
            var simulatedPointer = point;
            manager.ReadPointer = () => simulatedPointer;
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
            for (var i = 4; i <= 10; i++) eligible.Add(StartClient(i).Id);
            manager.SwitchProfile("10-man"); Pump();
            Check(manager.Accounts.Count == 10, "Switch to 10-man discovers all ten clients");
            manager.Swap(10); Pump(); AssertLayout(10);
            manager.ResetLayout(); manager.SaveCurrentPositions();
            Check(manager.Settings.Current.Slots[0].Width == 165, "Save current window positions reads native bounds");
            manager.Assign(2, manager.Accounts[3].Handle); Pump();
            Check(manager.Accounts[2].Pid == three.Id && manager.Accounts[3].Pid == replacement.Id, "Manual account assignment exchanges identities");
            manager.SwitchProfile("5-man"); Pump();
            Check(!manager.IsManaged(manager.Accounts[10].Handle), "5-man excludes extra clients from mouse focus and hotkeys");
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
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, "settings-preview.png"));
                ((Button)form.Controls.Find("save", true).Single()).PerformClick(); Pump();
                Check(edited?.DisplayName == "Notepad++" && edited.ActiveProfile == "10-man", "Settings editor saves custom name and selected profile");
            }
            // Install the actual low-level hook too; key routing above is exercised without synthetic input.
            using (var actualHook = new KeyboardHook(manager, _ => { }, () => { }))
                Check(true, "Native keyboard hook installs and disposes");
            var mouseEvents = 0;
            using (var mouseHook = new MouseHook(() => mouseEvents++))
            {
                Check(true, "Native mouse hook installs and disposes");
                if (!liveApp)
                {
                    Native.GetCursorPos(out var beforeMove);
                    Native.SetCursorPos(beforeMove.X + 2, beforeMove.Y); Pump();
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
