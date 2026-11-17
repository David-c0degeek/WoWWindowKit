namespace WoWWindowKit;

internal sealed class ControlsForm : Form
{
    private readonly WindowManager manager;
    private readonly Action<Action> run;
    private readonly Action<string> switchProfile;
    private readonly Action toggleMouse, togglePause;
    private readonly ComboBox profile = new() { Name = "controlProfile", DropDownStyle = ComboBoxStyle.DropDownList, Width = 105 };
    private readonly CheckBox mouseFocus = new() { Text = "Mouse focus", AutoSize = true };
    private readonly Button pause = new() { Text = "Pause", AutoSize = true };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly DataGridView slots = new() { Name = "controlSlots", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly ComboBox windows = new() { Name = "controlWindows", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Button catchWindow = new() { Name = "controlCatch", Text = "Catch / assign", AutoSize = true };
    private readonly Button forget = new() { Name = "controlForget", Text = "Forget", AutoSize = true };
    private readonly Button main = new() { Name = "controlMain", Text = "Make main", AutoSize = true };
    private readonly Button restartClient = new() { Name = "controlRestart", Text = "Restart client", AutoSize = true };
    private readonly Func<string, bool> confirm;
    private bool updating;
    internal ControlsForm(WindowManager manager, Action<Action> run, Action<string> switchProfile, Action toggleMouse, Action togglePause, Action settings, Action exit, Func<string, bool>? confirm = null)
    {
        this.manager = manager; this.run = run; this.switchProfile = switchProfile;
        this.confirm = confirm ?? (text => DarkTheme.Confirm(this, text, manager.Settings.DisplayName, "Close client", "Keep it"));
        this.toggleMouse = toggleMouse; this.togglePause = togglePause;
        Text = manager.Settings.DisplayName; Font = DarkTheme.CreateFont();
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(660, 500); MinimumSize = new Size(600, 440); Icon = Program.AppIcon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 7 };
        // Button rows size to their buttons, so larger fonts or DPI cannot clip them.
        foreach (var height in new[] { -1, 28, 0, 27, 35, -1, -1 })
            root.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : height < 0 ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        toolbar.Controls.AddRange([profile, mouseFocus, pause]); root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(status, 0, 1);
        slots.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", HeaderText = "Slot", FillWeight = 35 });
        slots.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Key", FillWeight = 50 });
        slots.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "WoW window", FillWeight = 270 });
        slots.RowTemplate.Height = 28; root.Controls.Add(slots, 0, 2);
        root.Controls.Add(new Label { Text = "Select a slot above, then choose a running window:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 3);
        root.Controls.Add(windows, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        actions.Controls.AddRange([catchWindow, forget, main, restartClient]);
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => run(() => { manager.Rescan(); RefreshState(); RefreshWindows(); });
        actions.Controls.Add(refresh); root.Controls.Add(actions, 0, 5);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var edit = new Button { Text = "Settings / layout", AutoSize = true };
        edit.Click += (_, _) => settings();
        var hide = new Button { Text = "Hide to tray", AutoSize = true };
        hide.Click += (_, _) => Hide();
        var quit = new Button { Name = "controlExit", Text = "Exit", AutoSize = true };
        quit.Click += (_, _) => exit();
        footer.Controls.AddRange([edit, hide, quit]); root.Controls.Add(footer, 0, 6);
        profile.SelectedIndexChanged += (_, _) => { if (!updating && profile.SelectedItem is string name) run(() => this.switchProfile(name)); };
        mouseFocus.CheckedChanged += (_, _) => { if (!updating) run(this.toggleMouse); };
        pause.Click += (_, _) => run(this.togglePause);
        windows.DropDown += (_, _) => run(RefreshWindows);
        windows.SelectedIndexChanged += (_, _) => UpdateButtons();
        slots.SelectionChanged += (_, _) => { if (!updating) UpdateButtons(); };
        catchWindow.Click += (_, _) => run(() =>
        {
            if (SelectedSlot is not { } slot || windows.SelectedItem is not WindowChoice choice) return;
            if (!choice.Window.Valid) throw new InvalidOperationException("That WoW window closed. Click Refresh and choose another window.");
            manager.Assign(slot, choice.Window.Handle); RefreshState(); RefreshWindows();
        });
        forget.Click += (_, _) => run(() => { if (SelectedSlot is { } slot) manager.Forget(slot); RefreshState(); RefreshWindows(); });
        main.Click += (_, _) => run(() => { if (SelectedSlot is { } slot) manager.Swap(slot); RefreshState(); });
        restartClient.Click += (_, _) => run(() =>
        {
            if (manager.RestartingAccount != null) manager.CancelRestart();
            else if (SelectedSlot is { } slot && manager.Accounts.TryGetValue(slot, out var client) && client.Valid
                && this.confirm($"Close WoW for account {slot} (PID {client.Pid})?\r\n\r\nThe client closes as with Alt+F4. Then start WoW again yourself and log in: the next WoW client that opens takes account {slot}'s place. Other clients are not touched."))
                manager.BeginRestart(slot, client);
            RefreshState(); RefreshWindows();
        });
        FormClosing += (_, e) =>
        {
            // WM_CLOSE from another process can be classified as TaskManagerClosing.
            if (e.CloseReason is CloseReason.UserClosing or CloseReason.TaskManagerClosing or CloseReason.None)
            { e.Cancel = true; Hide(); }
        };
        DarkTheme.Apply(this); RefreshState();
    }
    private int? SelectedSlot => slots.CurrentRow?.Cells[0].Value is int number ? number : null;
    internal void RefreshState()
    {
        if (IsDisposed) return;
        updating = true;
        try
        {
            Text = manager.Settings.DisplayName;
            var names = manager.Settings.Profiles.Select(p => p.Name).ToArray();
            if (!profile.Items.Cast<string>().SequenceEqual(names)) { profile.Items.Clear(); profile.Items.AddRange(names); }
            profile.SelectedItem = manager.Settings.ActiveProfile;
            mouseFocus.Checked = manager.Settings.MouseFocus; pause.Text = manager.Paused ? "Resume" : "Pause";
            status.Text = manager.Paused ? "Paused · " + manager.Status : manager.Status;
            var selected = SelectedSlot ?? 1;
            // Avoid replacing rows while the user is selecting one.
            if (slots.Rows.Count != manager.Settings.Current.Slots.Count)
            {
                slots.Rows.Clear();
                for (var i = 1; i <= manager.Settings.Current.Slots.Count; i++) slots.Rows.Add(i, "", "");
            }
            for (var i = 0; i < slots.Rows.Count; i++)
            {
                manager.Accounts.TryGetValue(i + 1, out var client);
                slots.Rows[i].Cells[1].Value = manager.Settings.Current.Slots[i].Hotkey;
                slots.Rows[i].Cells[2].Value = manager.RestartingAccount == i + 1 ? (manager.RestartAwaitingClient ? "Restarting · waiting for the next WoW client" : "Restarting · closing WoW")
                    : client?.Valid != true ? "Empty"
                    : $"{(manager.MainAccount == i + 1 ? "Main · " : "")}PID {client.Pid} · {client.Title}{(manager.GraphicsResetSuspected(client) ? " · frame restored by game, suspected graphics reset: restart may help" : "")}";
            }
            if (slots.CurrentRow == null && slots.Rows.Count > 0) slots.CurrentCell = slots.Rows[Math.Min(selected, slots.Rows.Count) - 1].Cells[0];
        }
        finally { updating = false; }
        UpdateButtons();
    }
    internal void RefreshWindows()
    {
        var selected = (windows.SelectedItem as WindowChoice)?.Window.Handle;
        windows.Items.Clear();
        var available = manager.Discover().OrderByDescending(w => w.StartTicks).ToList();
        foreach (var window in available)
        {
            windows.Items.Add(new WindowChoice(window, manager.Describe(window, newest: window == available[0])));
        }
        windows.SelectedItem = windows.Items.Cast<WindowChoice>().FirstOrDefault(c => c.Window.Handle == selected);
        if (windows.SelectedIndex < 0 && windows.Items.Count > 0) windows.SelectedIndex = 0;
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        var slot = SelectedSlot;
        var assigned = slot.HasValue && manager.Accounts.TryGetValue(slot.Value, out var client) && client.Valid;
        catchWindow.Enabled = slot.HasValue && windows.SelectedItem is WindowChoice choice && choice.Window.Valid;
        var restarting = manager.RestartingAccount != null;
        forget.Enabled = assigned && !restarting; main.Enabled = assigned && !manager.Paused;
        restartClient.Text = restarting ? "Cancel restart" : "Restart client";
        restartClient.Enabled = restarting || assigned && !manager.Editing;
    }
    private sealed record WindowChoice(ManagedWindow Window, string Label) { public override string ToString() => Label; }
}
