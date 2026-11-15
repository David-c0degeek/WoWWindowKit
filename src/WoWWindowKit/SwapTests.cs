namespace WoWWindowKit;

/// <summary>Delayed geometry, controlled time and focus; never touches the user's game clients.</summary>
internal static class SwapTests
{
    internal static void Run(WindowManager manager, Action<bool, string> check)
    {
        manager.ResetLayout();
        var read = manager.ReadBounds;
        var clock = manager.SwapClock;
        var position = manager.PositionSwapWindow;
        var activate = manager.ActivateWindow;
        var foreground = manager.ReadForeground;
        var trace = manager.TraceSwap;
        var quiet = manager.SwapQuietMs;
        var timeout = manager.SwapTimeoutMs;
        var hoverTimeout = manager.SettleTimeoutMs;
        var actual = manager.Accounts.Values.ToDictionary(w => w.Handle, w => read(w.Handle));
        var moves = new List<(nint Handle, Rectangle Bounds)>();
        var focused = new List<nint>();
        var log = new List<string>();
        long now = 10000;
        var applied = 0;
        void Tick(int ms = 0) { now += ms; manager.ProcessSwaps(); }
        void ApplyMoves()
        {
            foreach (var move in moves.Skip(applied)) actual[move.Handle] = move.Bounds;
            applied = moves.Count;
        }
        void Complete() { ApplyMoves(); Tick(); Tick(manager.SwapQuietMs); }
        void Fresh()
        {
            manager.ResetLayout();
            foreach (var window in manager.Accounts.Values) actual[window.Handle] = read(window.Handle);
            moves.Clear(); focused.Clear(); log.Clear(); applied = 0;
        }
        try
        {
            manager.ReadBounds = h => actual.GetValueOrDefault(h);
            manager.SwapClock = () => now;
            manager.PositionSwapWindow = (h, b) => moves.Add((h, b));
            manager.ActivateWindow = h => { focused.Add(h); return true; };
            manager.ReadForeground = () => manager.Accounts[1].Handle;
            manager.TraceSwap = log.Add;
            manager.SwapQuietMs = 50;
            manager.SwapTimeoutMs = 200;

            manager.Swap(2); manager.Swap(3); manager.Swap(1);
            check(manager.MainAccount == 2 && moves.Count == 2, "Delayed swap issues one batch and defers later geometry and focus");
            actual[moves[0].Handle] = moves[0].Bounds; Tick(80);
            check(moves.Count == 2 && manager.MainAccount == 2, "One settled window cannot release a queued transition");
            ApplyMoves(); Tick(); Tick(49);
            check(moves.Count == 2, "Queued swap waits for the full geometry quiet interval");
            actual[moves[0].Handle] = Rectangle.Empty; Tick(1);
            actual[moves[0].Handle] = moves[0].Bounds; Tick(); Tick(49);
            check(moves.Count == 2, "Geometry changing away from target restarts the quiet interval");
            Tick(1);
            check(manager.MainAccount == 1 && moves.Count == 4 && focused.All(h => h != manager.Accounts[3].Handle),
                "Burst keeps only the latest account selection, including return to account 1");
            Complete();
            check(log.Any(l => l.Contains("replaces #")) && log.Any(l => l.Contains("observed HWND"))
                && log.Count(l => l.Contains("geometry settled")) == 2, "Diagnostics distinguish replacement, observed geometry and settled transitions");
            var count = moves.Count; manager.Swap(1); Tick(100);
            check(moves.Count == count, "Selecting the current main does not resize any window");

            Fresh(); manager.Swap(2); manager.Swap(3); manager.Swap(2); Complete();
            check(manager.MainAccount == 2 && moves.Count == 2, "Selecting the in-flight main replaces a queued detour without extra resizes");

            Fresh(); manager.SettleTimeoutMs = 0; manager.Swap(2); manager.Swap(3);
            check(!manager.LayoutSettling(), "Hover focus retains its independent timeout");
            Tick(20);
            check(manager.MainAccount == 2 && moves.Count == 2, "Hover timeout cannot release another resize batch");
            manager.SettleTimeoutMs = hoverTimeout;
            Complete(); Complete();

            foreach (var suspend in new[] { "pause", "editing", "layout", "settings", "profile", "assignment" })
            {
                Fresh(); manager.Swap(2); manager.Swap(3);
                switch (suspend)
                {
                    case "pause": manager.Paused = true; manager.Paused = false; break;
                    case "editing": manager.Editing = true; manager.Editing = false; break;
                    case "layout": manager.ResetLayout(); break;
                    case "settings": manager.Settings = manager.Settings.Copy(); break;
                    case "profile": manager.SwitchProfile("5-man"); break;
                    case "assignment": manager.Assign(2, manager.Accounts[3].Handle); break;
                }
                var after = moves.Count; Complete(); Tick(300);
                check(moves.Count == after && !log.Any(l => l.Contains("starting: main 2 -> 3")), $"{suspend} cancels deferred account selection");
                if (suspend == "assignment") manager.Assign(2, manager.Accounts[3].Handle); // exchange back
            }

            Fresh(); manager.Swap(2); manager.Swap(3); Tick(201);
            check(log.Count(l => l.Contains("timed out after")) == 1 && log.Any(l => l.Contains("cancelled:") && l.Contains("timed out")),
                "Timeout is logged and drops the queued selection");
            Tick(300); count = moves.Count;
            check(!manager.Swap(1) && moves.Count == count && log.Count(l => l.Contains("timed out after")) == 1,
                "Timed-out moves fence further resizes without retry or repeated timeout logs");
            Complete(); Tick(300);
            check(manager.MainAccount == 2 && moves.Count == count, "Late geometry completion never replays the discarded selection");
            check(manager.Swap(1) && manager.MainAccount == 1, "New explicit swap works after the timed-out moves finally settle");
            Complete();

            Fresh(); manager.Swap(2); manager.Swap(3, requireGameForeground: true);
            manager.ReadForeground = static () => 0;
            Complete();
            check(manager.MainAccount == 2 && moves.Count == 2 && log.Any(l => l.Contains("target unavailable or management suspended")),
                "Leaving the game cancels a deferred hotkey instead of stealing focus back");
            manager.ReadForeground = () => manager.Accounts[1].Handle;

            Fresh(); manager.Swap(2); manager.Swap(3);
            var original = manager.Accounts[3];
            manager.Accounts[3] = manager.Accounts[1]; // simulate a changed assignment without the normal cancellation path
            Complete(); manager.Accounts[3] = original; manager.Publish();
            check(manager.MainAccount == 2 && moves.Count == 2, "Deferred account is revalidated against its original window identity");

            Fresh(); manager.Swap(2); manager.Swap(3);
            var lost = manager.Accounts[2]; manager.Accounts.Remove(2);
            Tick(); manager.Accounts[2] = lost; manager.Publish();
            check(log.Any(l => l.Contains("lost a managed window")) && moves.Count == 2,
                "Losing a window cancels queued work rather than treating disappearance as settlement");

            Fresh(); manager.Swap(2); manager.Swap(3);
            actual[moves[0].Handle] = null; Tick(40);
            check(moves.Count == 2 && log.Any(l => l.Contains("unavailable")), "Unreadable geometry defers queued moves and is diagnosed");

            Fresh(); manager.PositionSwapWindow = static (_, _) => throw new InvalidOperationException("simulated placement failure");
            var failed = false;
            try { manager.Swap(2); } catch (InvalidOperationException e) when (e.Message == "simulated placement failure") { failed = true; }
            manager.PositionSwapWindow = (h, b) => moves.Add((h, b));
            check(failed && manager.MainAccount == 1 && !manager.LayoutSettling() && manager.Swap(3),
                "Placement failure preserves the logical main and clears scheduler state for the next request");
            Complete();
        }
        finally
        {
            manager.Paused = false; manager.Editing = false;
            manager.ReadBounds = read; manager.SwapClock = clock; manager.PositionSwapWindow = position;
            manager.ActivateWindow = activate; manager.ReadForeground = foreground; manager.TraceSwap = trace;
            manager.SwapQuietMs = quiet; manager.SwapTimeoutMs = timeout; manager.SettleTimeoutMs = hoverTimeout;
            manager.ResetLayout();
        }
    }
}
