namespace WoWWindowKit;

/// <summary>
/// Owns the low-level keyboard and mouse hooks on a dedicated thread. Windows holds every
/// keystroke and mouse event system-wide until a low-level hook callback returns, so the
/// callbacks never do file I/O and never wait behind UI work, window scans or focus
/// changes. The keyboard callback reads the published snapshot and posts to the UI thread.
/// The mouse callback also gates Windows' tracking under <c>WindowManager.trackingLock</c>,
/// which every holder keeps only for state checks plus one SystemParametersInfo call.
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
        Native.PostThreadMessage(threadId, Native.WmQuit, 0, 0);
        thread.Join(2000);
    }
}

/// <summary>
/// Runs hover-focus decisions and every production window activation off both the UI and
/// hook threads. Activating a busy game window can block for seconds; here that delays only
/// the next activation and focus decision, never input delivery or the UI thread.
/// </summary>
internal sealed class FocusWorker : IDisposable
{
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private volatile bool stopping;
    private volatile int pollMs;
    private Action? job;

    internal FocusWorker(Action tick, int pollMs)
    {
        this.pollMs = pollMs;
        thread = new Thread(() =>
        {
            while (!stopping)
            {
                // Mouse events and queued jobs wake immediately; the timeout is the periodic fallback.
                wake.WaitOne(this.pollMs);
                if (stopping) break;
                // A queued activation (keyboard swap, controls) runs before the next hover decision.
                if (Interlocked.Exchange(ref job, null) is { } next)
                {
                    try { next(); }
                    catch (Exception ex) { Program.Log(ex); }
                    if (stopping) break;
                }
                try { tick(); }
                catch (Exception ex) { Program.Log(ex); }
            }
        }) { IsBackground = true, Name = "Mouse focus", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    internal int PollMs { set => pollMs = value; }

    /// <summary>Non-blocking; safe to call from a hook callback. Repeated wakes coalesce.</summary>
    internal void Wake()
    {
        if (stopping) return;
        try { wake.Set(); } catch (ObjectDisposedException) { /* stopped */ }
    }

    /// <summary>
    /// Queues one job for the worker and returns immediately, even while the worker is blocked
    /// in an activation. Only the latest job is kept; it must revalidate its target when it runs.
    /// </summary>
    internal void Run(Action action)
    {
        if (stopping) return;
        Volatile.Write(ref job, action);
        Wake();
    }

    public void Dispose()
    {
        if (stopping) return;
        stopping = true;
        Volatile.Write(ref job, null);
        wake.Set();
        // Keep the event alive if the worker is stuck in an activation call.
        if (thread.Join(2000)) wake.Dispose();
    }
}
