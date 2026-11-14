using System.ComponentModel;

namespace WoWWindowKit;

internal sealed class SettingsForm : Form
{
    private readonly WindowManager manager;
    private readonly AppSettings draft;
    private readonly Action<AppSettings> save;
    private readonly TextBox displayName = new() { Name = "displayName", Width = 230 };
    private readonly ComboBox profiles = new() { Name = "profiles", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly CheckBox mouseFocus = new() { Text = "Focus follows mouse over WoW", AutoSize = true };
    private readonly CheckBox borderless = new() { Text = "Borderless windows", AutoSize = true };
    private readonly HotkeyGrid grid = new() { Name = "layout", Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
    private readonly HotkeyBox toggleKey = new() { Name = "toggleKey", Width = 150, ReadOnly = true };
    private readonly Label hint = new() { Name = "hint", Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleLeft };
    private const string DefaultHint = "To change a key, select it and press the new combination. Mouse focus and hotkeys pause while this window is open.";
    private readonly FlowLayoutPanel assignments = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly LayoutPreview preview;
    private bool loading;
    internal SettingsForm(WindowManager manager, Action<AppSettings> save)
    {
        this.manager = manager; this.save = save; draft = manager.Settings.Copy();
        Text = draft.DisplayName; Font = new Font("Segoe UI", 10); StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(850, 740); Size = new Size(940, 820); AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Program.AppIcon;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(root);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill };
        top.Controls.Add(new Label { Text = "App name", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        displayName.Text = draft.DisplayName; top.Controls.Add(displayName);
        top.Controls.Add(new Label { Text = "   Layout", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        profiles.Items.AddRange(draft.Profiles.Select(p => (object)p.Name).ToArray()); profiles.SelectedItem = draft.ActiveProfile; top.Controls.Add(profiles);
        root.Controls.Add(top, 0, 0);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill };
        mouseFocus.Checked = draft.MouseFocus; borderless.Checked = draft.Borderless;
        toggleKey.Text = draft.MouseToggleHotkey;
        toggleKey.HotkeyPressed = CaptureToggleKey;
        options.Controls.AddRange([mouseFocus, borderless, new Label { Text = "   Mouse focus toggle key", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, toggleKey]);
        root.Controls.Add(options, 0, 1);
        preview = new LayoutPreview(() => draft.Current) { Dock = DockStyle.Fill }; root.Controls.Add(preview, 0, 2);
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Slot", Name = "Slot", ReadOnly = true, FillWeight = 45 });
        foreach (var name in new[] { "X", "Y", "Width", "Height", "Hotkey" })
            // Swap keys are recorded by pressing them, not typed (HotkeyGrid).
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name == "Hotkey" ? "Account swap key (press to set)" : name, DataPropertyName = name, Name = name, FillWeight = name == "Hotkey" ? 150 : 80, ReadOnly = name == "Hotkey" });
        grid.HotkeyPressed = CaptureSlotKey;
        grid.DataError += (_, e) => { e.ThrowException = false; DarkTheme.Message(this, "Use whole numbers for coordinates and sizes.", Text); };
        grid.CellValueChanged += (_, _) => preview.Invalidate();
        grid.DataBindingComplete += (_, _) => { foreach (DataGridViewRow row in grid.Rows) row.Cells[0].Value = row.Index + 1; };
        root.Controls.Add(grid, 0, 3);
        root.Controls.Add(new Label { Text = "Account assignment — numbers stay with accounts when swapping", AutoSize = true, Padding = new Padding(0, 8, 0, 0) }, 0, 4);
        root.Controls.Add(assignments, 0, 5);
        ShowHint(DefaultHint, false); root.Controls.Add(hint, 0, 6);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Name = "save", Text = "Save && apply", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        apply.Click += (_, _) => SaveAndApply(); buttons.Controls.AddRange([apply, cancel]); root.Controls.Add(buttons, 0, 7);
        AcceptButton = apply; CancelButton = cancel;
        profiles.SelectedIndexChanged += (_, _) =>
        {
            if (loading) return;
            if (!grid.EndEdit()) { loading = true; profiles.SelectedItem = draft.ActiveProfile; loading = false; return; }
            draft.ActiveProfile = (string)profiles.SelectedItem!; BindLayout();
        };
        BindLayout(); DarkTheme.Apply(this);
    }
    private void ShowHint(string text, bool warning)
    {
        hint.Text = text;
        hint.ForeColor = warning ? Color.FromArgb(240, 180, 90) : SystemColors.GrayText;
    }
    // Plain navigation keys keep their normal meaning in the grid and dialog.
    private static bool IsNavigation(KeyBinding binding) =>
        !binding.Ctrl && !binding.Alt && (Keys)binding.Key is Keys.Tab or Keys.Enter or Keys.Escape or Keys.Left or Keys.Up or Keys.Right or Keys.Down;
    internal bool CaptureSlotKey(int row, KeyBinding binding)
    {
        if (binding.Problem() is { } problem)
        {
            if (IsNavigation(binding)) return false;
            ShowHint($"{binding} can't be used: {problem}", true); return true;
        }
        if (binding == KeyBinding.Parse(draft.MouseToggleHotkey)) { ShowHint($"{binding} already toggles mouse focus.", true); return true; }
        var slots = draft.Current.Slots;
        var previous = slots[row].Hotkey;
        var message = $"Slot {row + 1} now swaps with {binding}.";
        for (var other = 0; other < slots.Count; other++)
        {
            // Taking another slot's key gives that slot this slot's old key, so the profile stays valid.
            if (other == row || KeyBinding.Parse(slots[other].Hotkey) != binding) continue;
            slots[other].Hotkey = previous;
            message += $" Slot {other + 1} takes {previous}.";
            ((BindingList<Slot>)grid.DataSource!).ResetItem(other);
        }
        slots[row].Hotkey = binding.ToString();
        ((BindingList<Slot>)grid.DataSource!).ResetItem(row);
        ShowHint(message, false); return true;
    }
    internal bool CaptureToggleKey(KeyBinding binding)
    {
        if (binding.Problem() is { } problem)
        {
            if (IsNavigation(binding)) return false;
            ShowHint($"{binding} can't be used: {problem}", true); return true;
        }
        var used = draft.Profiles.SelectMany(p => p.Slots.Select((slot, i) => (p.Name, Number: i + 1, slot.Hotkey)))
            .FirstOrDefault(x => KeyBinding.Parse(x.Hotkey) == binding);
        if (used.Hotkey != null) { ShowHint($"{binding} already swaps to slot {used.Number} in {used.Name}.", true); return true; }
        draft.MouseToggleHotkey = toggleKey.Text = binding.ToString();
        ShowHint($"Mouse focus now toggles with {binding}.", false); return true;
    }
    private void BindLayout()
    {
        grid.DataSource = new BindingList<Slot>(draft.Current.Slots);
        preview.Invalidate(); assignments.Controls.Clear();
        var available = manager.Discover();
        for (var account = 1; account <= draft.Current.Slots.Count; account++)
        {
            var number = account;
            var row = new FlowLayoutPanel { Width = 810, Height = 36, WrapContents = false };
            manager.Accounts.TryGetValue(number, out var current);
            row.Controls.Add(new Label { Text = $"{number}  {(current == null ? "Not running" : $"PID {current.Pid} · {current.Title}")}", Width = 340, AutoEllipsis = true, Padding = new Padding(0, 5, 0, 0) });
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
            foreach (var window in available) combo.Items.Add(new WindowChoice(window));
            combo.SelectedItem = combo.Items.Cast<WindowChoice>().FirstOrDefault(c => c.Window.Handle == current?.Handle);
            row.Controls.Add(combo);
            // Assignments are staged and committed only by Save & apply.
            combo.Tag = number;
            assignments.Controls.Add(row);
        }
        DarkTheme.Apply(assignments);
    }
    private void SaveAndApply()
    {
        try
        {
            if (!grid.EndEdit()) return;
            draft.DisplayName = displayName.Text.Trim(); draft.MouseFocus = mouseFocus.Checked; draft.Borderless = borderless.Checked;
            draft.Validate();
            var selections = assignments.Controls.Cast<FlowLayoutPanel>().SelectMany(p => p.Controls.OfType<ComboBox>())
                .Where(c => c.SelectedItem is WindowChoice).Select(c => ((int)c.Tag!, ((WindowChoice)c.SelectedItem!).Window.Handle)).ToList();
            if (selections.Select(s => s.Handle).Distinct().Count() != selections.Count)
                throw new InvalidDataException("Choose a different WoW window for each account.");
            if (selections.Any(s => !Native.IsWindow(s.Handle))) throw new InvalidOperationException("A selected WoW window closed. Cancel and reopen settings to refresh the list.");
            save(draft);
            foreach (var (number, handle) in selections)
                if (!manager.Accounts.TryGetValue(number, out var current) || current.Handle != handle) manager.Assign(number, handle);
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { DarkTheme.Message(this, ex.Message, Text); }
    }
    private sealed record WindowChoice(ManagedWindow Window) { public override string ToString() => $"PID {Window.Pid} · {Window.Title}"; }
}

/// <summary>Records a key combination when the swap-key column is selected.</summary>
internal sealed class HotkeyGrid : DataGridView
{
    internal Func<int, KeyBinding, bool>? HotkeyPressed;
    // ProcessCmdKey runs before grid navigation, dialog keys, mnemonics and F1 help.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        CurrentCell is { OwningColumn.Name: "Hotkey" } cell && !IsCurrentCellInEditMode && KeyBinding.TryFromKeys(keyData, out var binding) && HotkeyPressed?.Invoke(cell.RowIndex, binding) == true
        || base.ProcessCmdKey(ref msg, keyData);
}

/// <summary>Read-only box that records the key combination pressed while it has focus.</summary>
internal sealed class HotkeyBox : TextBox
{
    internal Func<KeyBinding, bool>? HotkeyPressed;
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        KeyBinding.TryFromKeys(keyData, out var binding) && HotkeyPressed?.Invoke(binding) == true || base.ProcessCmdKey(ref msg, keyData);
}

internal sealed class LayoutPreview(Func<LayoutProfile> profile) : Control
{
    internal LayoutPreview() : this(() => new LayoutProfile()) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(Color.FromArgb(27, 32, 42));
        var slots = profile().Slots;
        if (slots.Count == 0) return;
        var minX = slots.Min(s => s.X); var minY = slots.Min(s => s.Y);
        var width = Math.Max(1, slots.Max(s => (long)s.X + s.Width) - minX);
        var height = Math.Max(1, slots.Max(s => (long)s.Y + s.Height) - minY);
        var scale = Math.Min((ClientSize.Width - 20d) / width, (ClientSize.Height - 20d) / height);
        if (scale <= 0) return;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var rect = new Rectangle((int)((slot.X - (long)minX) * scale) + 10, (int)((slot.Y - (long)minY) * scale) + 10, Math.Max(1, (int)(slot.Width * scale) - 1), Math.Max(1, (int)(slot.Height * scale) - 1));
            using var brush = new SolidBrush(i == 0 ? Color.FromArgb(54, 116, 191) : Color.FromArgb(58, 72, 94));
            e.Graphics.FillRectangle(brush, rect);
            TextRenderer.DrawText(e.Graphics, i == 0 ? "1 · Main" : (i + 1).ToString(), Font, rect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
