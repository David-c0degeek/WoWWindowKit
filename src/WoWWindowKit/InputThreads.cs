namespace WoWWindowKit;

/// <summary>
/// Owns the low-level keyboard and mouse hooks on a dedicated thread. Windows holds every
/// keystroke and mouse event system-wide until a low-level hook callback returns, so the
/// callbacks must never wait behind UI work, window scans, or focus changes.
/// </summary>
internal sealed class HookThread : IDisposable
{
    private readonly Thread thread;
    private uint threadId;

    internal HookThread(Action<List<IDisposable>> install)
    {
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() =>
        {
            var hooks = new List<IDisposable>();
            // Create this thread's message queue before Dispose can post WM_QUIT to it.
            Native.PeekMessage(out _, 0, 0, 0, 0);
            threadId = Native.GetCurrentThreadId();
            try { install(hooks); }
            catch (Exception ex) { failure = ex; }
            ready.Set();
            try { if (failure == null) while (Native.GetMessage(out _, 0, 0, 0) > 0) { } }
            finally { foreach (var hook in hooks) hook.Dispose(); }
        }) { IsBackground = true, Name = "Input hooks", Priority = ThreadPriority.Highest };
        thread.Start();
        ready.Wait();
        if (failure != null) { thread.Join(); throw failure; }
    }

    public void Dispose()
    {
        if (!thread.IsAlive) return;
        Native.PostThreadMessage(threadId, 0x12, 0, 0); // WM_QUIT
        thread.Join(2000);
    }
}

/// <summary>
/// Runs hover-focus decisions off both the UI and hook threads. Activating a busy game window
/// can block; here that delays only the next focus decision, never input delivery.
/// </summary>
internal sealed class FocusWorker : IDisposable
{
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private volatile bool stopping;
    private volatile int pollMs;

    internal FocusWorker(Action tick, int pollMs)
    {
        this.pollMs = pollMs;
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                // Mouse events wake immediately; the timeout is the periodic fallback.
                wake.WaitOne(this.pollMs);
                if (stopping) break;
                try { tick(); }
                catch (Exception ex) { Program.Log(ex); }
            }
        }) { IsBackground = true, Name = "Mouse focus", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    internal int PollMs { set => pollMs = value; }

    /// <summary>Non-blocking; safe to call from a hook callback. Repeated wakes coalesce.</summary>
    internal void Wake() { if (!stopping) wake.Set(); }

    public void Dispose()
    {
        if (stopping) return;
        stopping = true;
        wake.Set();
        // Keep the event alive if the worker is stuck in an activation call.
        if (thread.Join(2000)) wake.Dispose();
    }
}
