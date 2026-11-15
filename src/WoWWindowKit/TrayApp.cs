namespace WoWWindowKit;

internal sealed class ControlWindow : NativeWindow, IDisposable
{
    internal const string WindowName = "WoWWindowKit.Control.1C825122";
    internal static readonly uint CommandMessage = Native.RegisterWindowMessage(WindowName);
    internal Action<int>? Command;
    internal ControlWindow() => CreateHandle(new CreateParams { Caption = WindowName });
    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == CommandMessage) Command?.Invoke((int)message.WParam);
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
    private SettingsForm? editor;
    private string lastError = "";
    private volatile bool disposed;
    internal TrayApp(AppSettings settings)
    {
        manager = new WindowManager(settings);
        DarkTheme.Apply(menu);
        _ = dispatcher.Handle;
        tray = new NotifyIcon { Icon = Program.AppIcon, ContextMenuStrip = menu, Visible = true };
        controls = new ControlsForm(manager, Safe, SetProfile, ToggleMouse, TogglePause, ShowSettings, ExitThread);
        focus = new FocusWorker(manager.MouseTick, settings.MousePollMs);
        tracking = new WindowTracking(Path.Combine(AppContext.BaseDirectory, "window-tracking.restore"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => tracking.Dispose();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => tracking.Dispose();
        manager.SystemTracking = tracking.Set;
        // Hook callbacks only read a snapshot, post to the UI thread, or wake the focus worker.
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
        commands.Command = command => dispatcher.BeginInvoke(() => Safe(() =>
        {
            if (command == -1) { ExitThread(); return; }
            if (command == 2) { ShowSettings(); return; }
            if (command is 5 or 10) SetProfile(command == 5 ? "5-man" : "10-man");
            ShowControls();
        }));
        timer.Tick += (_, _) => Safe(() =>
        {
            if (!manager.Paused) { manager.Rescan(); manager.ObserveLayout(); }
            UpdateTray();
        });
        swapTimer.Tick += (_, _) => Safe(manager.ProcessSwaps);
        UpdateTray(); Safe(manager.Rescan); timer.Start(); swapTimer.Start();
    }
    /// <summary>Queues work for the UI thread from any thread without waiting.</summary>
    private void Post(Action action)
    {
        if (disposed) return;
        try { dispatcher.BeginInvoke(() => { if (!disposed) Safe(action); }); }
        catch (InvalidOperationException) { /* shutting down */ }
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
            item.Click += (_, _) => Safe(() => SetProfile(name)); menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        WindowMenus.AddTo(menu, manager, Safe, editor == null);
        menu.Items.Add(new ToolStripSeparator());
        var mouse = new ToolStripMenuItem($"Focus follows mouse  ({manager.Settings.MouseToggleHotkey})") { Checked = manager.Settings.MouseFocus };
        mouse.Click += (_, _) => Safe(ToggleMouse); menu.Items.Add(mouse);
        var pause = new ToolStripMenuItem("Pause app") { Checked = manager.Paused };
        pause.Click += (_, _) => Safe(TogglePause); menu.Items.Add(pause);
        menu.Items.Add("Reapply layout / reset main", null, (_, _) => Safe(() => { manager.Rescan(); manager.ResetLayout(); }));
        menu.Items.Add("Save current window positions", null, (_, _) => Safe(() => { manager.SaveCurrentPositions(); manager.Settings.Save(AppSettings.FilePath); tray.ShowBalloonTip(2500, manager.Settings.DisplayName, "Current window positions saved to this profile.", ToolTipIcon.Info); }));
        menu.Items.Add("Settings && layout…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit and restore windows", null, (_, _) => ExitThread());
    }
    private void UpdateTray()
    {
        var text = $"{manager.Settings.DisplayName} · {manager.Settings.ActiveProfile}{(manager.Paused ? " · Paused" : "")}";
        tray.Text = text.Length > 63 ? text[..63] : text;
        controls.RefreshState();
    }
    private void SetProfile(string name)
    {
        if (editor is { IsDisposed: false }) { editor.Activate(); return; }
        manager.SwitchProfile(name); manager.Settings.Save(AppSettings.FilePath); UpdateTray();
    }
    private void ToggleMouse() { manager.Settings.MouseFocus = !manager.Settings.MouseFocus; manager.Publish(); manager.Settings.Save(AppSettings.FilePath); UpdateTray(); }
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
            if (editor is { IsDisposed: false }) { editor.Activate(); return; }
            controls.RefreshState(); controls.RefreshWindows();
            if (controls.WindowState == FormWindowState.Minimized) controls.WindowState = FormWindowState.Normal;
            controls.Show(); Native.ShowWindow(controls.Handle, 5); Native.Activate(controls.Handle);
        });
    }
    internal void ShowSettings()
    {
        Safe(() =>
        {
            if (editor is { IsDisposed: false }) { editor.Activate(); return; }
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
                    focus.PollMs = draft.MousePollMs; UpdateTray();
                });
                editor = form;
                // A hidden launcher must not hide an explicitly requested Settings window.
                form.Shown += (_, _) => { Native.ShowWindow(form.Handle, 1); Native.Activate(form.Handle); };
                form.ShowDialog();
            }
            finally { editor = null; manager.Editing = false; }
        });
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
            disposed = true; swapTimer.Stop(); hooks.Dispose(); focus.Dispose(); tracking.Dispose(); editor?.Close(); controls.Dispose(); timer.Stop(); manager.Dispose();
            tray.Visible = false; tray.Dispose(); timer.Dispose(); swapTimer.Dispose(); menu.Dispose(); commands.Dispose(); dispatcher.Dispose();
        }
        base.ExitThreadCore();
    }
}
