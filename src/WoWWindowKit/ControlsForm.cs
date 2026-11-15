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
    private bool updating;
    internal ControlsForm(WindowManager manager, Action<Action> run, Action<string> switchProfile, Action toggleMouse, Action togglePause, Action settings, Action exit)
    {
        this.manager = manager; this.run = run; this.switchProfile = switchProfile;
        this.toggleMouse = toggleMouse; this.togglePause = togglePause;
        Text = manager.Settings.DisplayName; Font = DarkTheme.CreateFont();
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(660, 500); MinimumSize = new Size(600, 440); Icon = Program.AppIcon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 38, 28, 0, 27, 35, 39, 36 }) root.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(root);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        toolbar.Controls.AddRange([profile, mouseFocus, pause]); root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(status, 0, 1);
        slots.Columns.Add(new DataGridViewTextBoxColumn { Name = "Number", HeaderText = "Slot", FillWeight = 35 });
        slots.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Key", FillWeight = 50 });
        slots.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "WoW window", FillWeight = 270 });
        slots.RowTemplate.Height = 28; root.Controls.Add(slots, 0, 2);
        root.Controls.Add(new Label { Text = "Select a slot above, then choose a running window:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 3);
        root.Controls.Add(windows, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.AddRange([catchWindow, forget, main]);
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => run(() => { manager.Rescan(); RefreshState(); RefreshWindows(); });
        actions.Controls.Add(refresh); root.Controls.Add(actions, 0, 5);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
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
                slots.Rows[i].Cells[2].Value = client?.Valid == true ? $"{(manager.MainAccount == i + 1 ? "Main · " : "")}PID {client.Pid} · {client.Title}" : "Empty";
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
        forget.Enabled = assigned; main.Enabled = assigned && !manager.Paused;
    }
    private sealed record WindowChoice(ManagedWindow Window, string Label) { public override string ToString() => Label; }
}
