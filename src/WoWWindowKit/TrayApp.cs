using System.Runtime.InteropServices;

namespace WoWWindowKit;

/// <summary>Hidden window that later launches find by title and send WM_COPYDATA commands to.</summary>
internal sealed class ControlWindow : NativeWindow, IDisposable
{
    // A profile name arrives from a command line, which Windows limits to 32767 characters.
    private const int MaxProfileChars = 32767;
    /// <summary>Raised on the UI thread inside the sender's SendMessage; handlers must not block.</summary>
    internal Action<ControlCommand, string?>? Command;
    internal ControlWindow() => CreateHandle(new CreateParams { Caption = Program.ControlWindowName });
    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == Native.WmCopyData && message.LParam != 0)
        {
            var data = Marshal.PtrToStructure<Native.CopyData>(message.LParam);
            var command = (ControlCommand)(int)data.Data;
            if (Enum.IsDefined(command) && data.Size is >= 0 and <= MaxProfileChars * sizeof(char) && data.Size % sizeof(char) == 0)
            {
                var profile = data.Size == 0 ? null : Marshal.PtrToStringUni(data.Pointer, data.Size / sizeof(char));
                Command?.Invoke(command, profile);
                message.Result = 1;
            }
            return;
        }
        base.WndProc(ref message);
    }
    public void Dispose() => DestroyHandle();
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly WindowManager manager;
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer swapTimer = new() { Interval = 25 };
    private readonly HookThread hooks;
    private readonly FocusWorker focus;
    private readonly WindowTracking tracking;
    private readonly ControlWindow commands = new();
    private readonly Control dispatcher = new();
    private readonly ControlsForm controls;
    private readonly string? startupWarning;
    private SettingsForm? editor;
    private string lastError = "";
    private volatile bool disposed;
    internal TrayApp(AppSettings settings)
    {
        manager = new WindowManager(settings);
        DarkTheme.Apply(menu);
        _ = dispatcher.Handle;
        // Made visible only once construction succeeded, so a failed start leaves no stale icon.
        tray = new NotifyIcon { Icon = Program.AppIcon, ContextMenuStrip = menu };
        controls = new ControlsForm(manager, Safe, SetProfile, ToggleMouse, TogglePause, ShowSettings, ExitThread);
        focus = new FocusWorker(manager.MouseTick, settings.MousePollMs);
        // Every production activation runs on the focus worker; the job revalidates its target there.
        manager.RequestActivation = focus.Run;
        // Records the original values here on the UI thread, before any hook can enable tracking.
        tracking = new WindowTracking(Path.Combine(AppContext.BaseDirectory, "window-tracking.restore"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tracking.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => tracking.Dispose();
        if (tracking.Unavailable is { } reason)
            startupWarning = $"Windows' active window tracking is off for this run ({reason}). The app activates hovered clients itself. Keep the application folder writable.";
        else
        {
            // Raised on the hook thread: logging and the balloon run on the UI thread.
            tracking.Failed = error => Post(() => Warn($"Windows' active window tracking was turned off for this run: {error.Message} The app activates hovered clients itself."));
            manager.SystemTracking = tracking.Set;
        }
        // Hook callbacks never do I/O. Keys read the snapshot and post to the UI thread; mouse
        // moves gate Windows' tracking (a short trackingLock section) and wake the focus worker.
        hooks = new HookThread(installed =>
        {
            installed.Add(new KeyboardHook(manager, account => Post(() => manager.Swap(account, requireGameForeground: true)), () => Post(ToggleMouse)));
            installed.Add(new MouseHook(point => { manager.PointerMoved(point); focus.Wake(); }));
        });
        manager.Changed = UpdateTray;
        manager.FocusWarningChanged = () => Post(UpdateTray);
        tray.DoubleClick += (_, _) => ShowControls();
        menu.Opening += (_, _) => { manager.Editing = true; Safe(BuildMenu); };
        menu.Closed += (_, _) => { if (editor == null) manager.Editing = false; };
        commands.Command = (command, profile) => dispatcher.BeginInvoke(() => Safe(() => HandleCommand(command, profile)));
        timer.Tick += (_, _) => Safe(() =>
        {
            if (!manager.Paused) { manager.Rescan(); manager.ObserveLayout(); }
            UpdateTray();
        });
        swapTimer.Tick += (_, _) => Safe(manager.ProcessSwaps);
        UpdateTray();
        Safe(manager.Rescan);
        timer.Start();
        swapTimer.Start();
        tray.Visible = true;
        if (startupWarning != null) Warn(startupWarning);
    }
    /// <summary>Queues work for the UI thread from any thread without waiting.</summary>
    private void Post(Action action)
    {
        if (disposed) return;
        try { dispatcher.BeginInvoke(() => { if (!disposed) Safe(action); }); }
        catch (InvalidOperationException) { /* shutting down */ }
    }
    /// <summary>A later launch: switch to any existing profile, then show what it asked for.</summary>
    private void HandleCommand(ControlCommand command, string? profile)
    {
        if (command == ControlCommand.Exit) { ExitThread(); return; }
        if (profile != null)
        {
            if (manager.Settings.FindProfile(profile) is { } name) SetProfile(name);
            else Warn($"Profile '{profile}' was not found. Available: {string.Join(", ", manager.Settings.Profiles.Select(p => p.Name))}.");
        }
        if (command == ControlCommand.ShowSettings) ShowSettings();
        else if (command == ControlCommand.ShowControls) ShowControls();
    }
    /// <summary>UI thread: logs the text and shows it as a tray warning.</summary>
    private void Warn(string text)
    {
        Program.LogLine(text);
        // Balloon text is limited to 255 characters; the full text is in app.log.
        tray.ShowBalloonTip(5000, manager.Settings.DisplayName, text.Length > 255 ? text[..254] + "…" : text, ToolTipIcon.Warning);
    }
    private void BuildMenu()
    {
        while (menu.Items.Count > 0) { var item = menu.Items[0]; menu.Items.RemoveAt(0); item.Dispose(); }
        menu.Items.Add(new ToolStripMenuItem(manager.Settings.DisplayName) { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(manager.Status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show controls", null, (_, _) => ShowControls());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var profile in manager.Settings.Profiles)
        {
            var name = profile.Name;
            var item = new ToolStripMenuItem(name) { Checked = manager.Settings.ActiveProfile == name };
            item.Click += (_, _) => Safe(() => SetProfile(name));
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        WindowMenus.AddTo(menu, manager, Safe, editor == null);
        menu.Items.Add(new ToolStripSeparator());
        var mouse = new ToolStripMenuItem($"Focus follows mouse  ({manager.Settings.MouseToggleHotkey})") { Checked = manager.Settings.MouseFocus };
        mouse.Click += (_, _) => Safe(ToggleMouse);
        menu.Items.Add(mouse);
        var pause = new ToolStripMenuItem("Pause app") { Checked = manager.Paused };
        pause.Click += (_, _) => Safe(TogglePause);
        menu.Items.Add(pause);
        menu.Items.Add("Reapply layout / reset main", null, (_, _) => Safe(() => { manager.Rescan(); manager.ResetLayout(); }));
        menu.Items.Add("Save current window positions", null, (_, _) => Safe(SaveCurrentPositions));
        menu.Items.Add("Settings && layout…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit and restore windows", null, (_, _) => ExitThread());
    }
    private void SaveCurrentPositions()
    {
        manager.SaveCurrentPositions();
        manager.Settings.Save(AppSettings.FilePath);
        tray.ShowBalloonTip(2500, manager.Settings.DisplayName, "Current window positions saved to this profile.", ToolTipIcon.Info);
    }
    private void UpdateTray()
    {
        var text = $"{manager.Settings.DisplayName} · {manager.Settings.ActiveProfile}{(manager.Paused ? " · Paused" : "")}";
        tray.Text = text.Length > 63 ? text[..63] : text;
        controls.RefreshState();
    }
    private void SetProfile(string name)
    {
        if (editor is { IsDisposed: false }) { ActivateOwn(editor); return; }
        manager.SwitchProfile(name);
        manager.Settings.Save(AppSettings.FilePath);
        UpdateTray();
    }
    private void ToggleMouse()
    {
        manager.Settings.MouseFocus = !manager.Settings.MouseFocus;
        manager.Publish();
        manager.Settings.Save(AppSettings.FilePath);
        UpdateTray();
    }
    private void TogglePause()
    {
        manager.Paused = !manager.Paused;
        if (!manager.Paused) { manager.Rescan(); manager.ApplyLayout(); }
        UpdateTray();
    }
    internal void ShowControls()
    {
        Safe(() =>
        {
            if (editor is { IsDisposed: false }) { ActivateOwn(editor); return; }
            controls.RefreshState();
            controls.RefreshWindows();
            if (controls.WindowState == FormWindowState.Minimized) controls.WindowState = FormWindowState.Normal;
            controls.Show();
            Native.ShowWindow(controls.Handle, Native.SwShow);
            ActivateOwn(controls);
        });
    }
    internal void ShowSettings()
    {
        Safe(() =>
        {
            if (editor is { IsDisposed: false }) { ActivateOwn(editor); return; }
            manager.Editing = true;
            try
            {
                using var form = new SettingsForm(manager, draft =>
                {
                    var previous = manager.Settings;
                    var main = manager.MainAccount;
                    manager.Settings = draft;
                    try { manager.ApplyLayout(draft.ActiveProfile == previous.ActiveProfile ? main : 1); draft.Save(AppSettings.FilePath); }
                    catch
                    {
                        manager.Settings = previous;
                        manager.ApplyLayout(main);
                        throw;
                    }
                    focus.PollMs = draft.MousePollMs;
                    UpdateTray();
                });
                editor = form;
                // A hidden launcher must not hide an explicitly requested Settings window.
                form.Shown += (_, _) => { Native.ShowWindow(form.Handle, Native.SwShowNormal); ActivateOwn(form); };
                form.ShowDialog();
            }
            finally { editor = null; manager.Editing = false; }
        });
    }
    /// <summary>
    /// Activates one of the app's own windows on the focus worker. Activation can attach to the
    /// foreground (possibly busy) game's input queue, which must never stall the UI thread.
    /// </summary>
    private void ActivateOwn(Form form)
    {
        var handle = form.Handle;
        focus.Run(() => { if (Native.IsWindow(handle) && Native.IsWindowVisible(handle)) Native.Activate(handle); });
    }
    private void Safe(Action action)
    {
        try { action(); lastError = ""; }
        catch (Exception ex)
        {
            Program.Log(ex);
            if (lastError != ex.Message) tray.ShowBalloonTip(5000, manager.Settings.DisplayName, ex.Message, ToolTipIcon.Warning);
            lastError = ex.Message;
        }
    }
    protected override void ExitThreadCore()
    {
        if (!disposed)
        {
            // Stop input threads before windows are restored so nothing re-activates them.
            disposed = true;
            swapTimer.Stop();
            hooks.Dispose();
            focus.Dispose();
            tracking.Dispose();
            editor?.Close();
            controls.Dispose();
            timer.Stop();
            manager.Dispose();
            tray.Visible = false;
            tray.Dispose();
            timer.Dispose();
            swapTimer.Dispose();
            menu.Dispose();
            commands.Dispose();
            dispatcher.Dispose();
        }
        base.ExitThreadCore();
    }
}
