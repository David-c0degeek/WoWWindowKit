namespace WoWWindowKit;

internal static class WindowMenus
{
    internal static void AddTo(ContextMenuStrip menu, WindowManager manager, Action<Action> run, bool enabled = true)
    {
        var catchMenu = new ToolStripMenuItem("Catch / assign WoW window") { Enabled = enabled };
        var forgetMenu = new ToolStripMenuItem("Forget WoW window") { Enabled = enabled };
        var available = manager.Discover().OrderByDescending(w => w.StartTicks).ToList();
        for (var number = 1; number <= manager.Settings.Current.Slots.Count; number++)
        {
            var slot = number;
            manager.Accounts.TryGetValue(slot, out var current);
            var label = $"Slot {slot} · {manager.Settings.Current.Slots[slot - 1].Hotkey}";
            var target = new ToolStripMenuItem(label + (current?.Valid == true ? $" · PID {current.Pid}" : " · Empty"));
            foreach (var window in available)
            {
                var selected = window;
                // '&' marks a mnemonic in menu text; titles may contain it.
                var choice = new ToolStripMenuItem(manager.Describe(selected, newest: selected == available[0]).Replace("&", "&&"))
                    { Checked = current?.Handle == selected.Handle, Tag = selected.Handle };
                choice.Click += (_, _) => run(() =>
                {
                    if (!selected.Valid) throw new InvalidOperationException("That WoW window closed. Reopen the tray menu to refresh the list.");
                    manager.Assign(slot, selected.Handle);
                });
                target.DropDownItems.Add(choice);
            }
            if (available.Count == 0) target.DropDownItems.Add(new ToolStripMenuItem("No running WoW windows found") { Enabled = false });
            DarkTheme.Apply(target.DropDown);
            catchMenu.DropDownItems.Add(target);
            if (current?.Valid == true)
            {
                var forget = new ToolStripMenuItem($"{label} · PID {current.Pid} · {current.Title.Replace("&", "&&")}");
                var expectedHandle = current.Handle;
                forget.Click += (_, _) => run(() =>
                {
                    if (manager.Accounts.TryGetValue(slot, out var existing) && existing.Handle == expectedHandle) manager.Forget(slot);
                });
                forgetMenu.DropDownItems.Add(forget);
            }
        }
        if (forgetMenu.DropDownItems.Count == 0) forgetMenu.DropDownItems.Add(new ToolStripMenuItem("No managed WoW windows") { Enabled = false });
        catchMenu.DropDownItems.Add(new ToolStripSeparator());
        catchMenu.DropDownItems.Add(new ToolStripMenuItem("Assignment returns windows to their numbered slots") { Enabled = false });
        DarkTheme.Apply(catchMenu.DropDown); DarkTheme.Apply(forgetMenu.DropDown);
        menu.Items.Add(catchMenu); menu.Items.Add(forgetMenu);
    }
}
