using System.Runtime.InteropServices;

namespace WoWWindowKit;

internal static class DarkTheme
{
    internal static readonly Color Background = Color.FromArgb(22, 27, 34);
    internal static readonly Color Surface = Color.FromArgb(33, 40, 51);
    internal static readonly Color Text = Color.FromArgb(230, 237, 243);
    internal static readonly Color Muted = Color.FromArgb(151, 164, 182);
    private static readonly Color Border = Color.FromArgb(57, 68, 83);
    private static readonly Color Accent = Color.FromArgb(45, 94, 153);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    internal static void Apply(Control root)
    {
        root.BackColor = root is TextBox or ComboBox or Button ? Surface : Background;
        root.ForeColor = Text;
        if (root is Form form)
        {
            void TitleBar()
            {
                var enabled = 1; DwmSetWindowAttribute(form.Handle, 20, ref enabled, 4);
                var caption = 0x00221B16; DwmSetWindowAttribute(form.Handle, 35, ref caption, 4);
                var text = 0x00F3EDE6; DwmSetWindowAttribute(form.Handle, 36, ref text, 4);
            }
            if (form.IsHandleCreated) TitleBar();
            else form.HandleCreated += (_, _) => TitleBar();
        }
        if (root is Button button)
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Accent;
        }
        if (root is DataGridView grid)
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = Background; grid.GridColor = Border;
            grid.DefaultCellStyle.BackColor = Surface; grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Accent; grid.DefaultCellStyle.SelectionForeColor = Text;
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(28, 35, 45);
            grid.ColumnHeadersDefaultCellStyle.BackColor = Background; grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Background;
        }
        if (root is ToolStrip strip) strip.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        foreach (Control child in root.Controls) Apply(child);
    }
    internal static void Message(IWin32Window? owner, string message, string title)
    {
        using var dialog = new Form { Text = title, Font = new Font("Segoe UI", 10), Size = new Size(540, 280), MinimumSize = new Size(420, 220), StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), RowCount = 2, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        panel.Controls.Add(new TextBox { Text = message, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, TabStop = false }, 0, 0);
        var close = new Button { Text = "OK", DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right, Size = new Size(90, 32) };
        panel.Controls.Add(close, 0, 1); dialog.Controls.Add(panel); dialog.AcceptButton = close; dialog.CancelButton = close;
        Apply(dialog); dialog.Shown += (_, _) => Native.ShowWindow(dialog.Handle, 1);
        dialog.ShowDialog(owner);
    }
    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuItemSelected => Surface;
        public override Color MenuItemBorder => Border;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
        public override Color CheckBackground => Accent;
        public override Color CheckSelectedBackground => Accent;
        public override Color CheckPressedBackground => Accent;
    }
}
