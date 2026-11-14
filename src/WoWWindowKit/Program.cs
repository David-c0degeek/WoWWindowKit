namespace WoWWindowKit;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        // Deliberately opt into WinForms' explicit dark mode, independent of the OS preference.
#pragma warning disable WFO5001
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        if (args.Contains("--test-client"))
        {
            var number = int.Parse(Option(args, "--test-client")!);
            using var client = new TestClientForm { Text = $"WoW {number}", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(80 + number * 8, 80 + number * 8, 280, 180) };
            Application.Run(client); return 0;
        }
        if (args.Contains("--self-test")) return SelfTest.Run();
        using var mutex = new Mutex(true, @"Local\WoWWindowKit.1C825122", out var first);
        var requestedProfile = Option(args, "--profile");
        if (!first)
        {
            nint existing;
            var deadline = Environment.TickCount64 + 5000;
            do
            {
                existing = Native.FindWindow(null, ControlWindow.WindowName);
                if (existing != 0) break;
                Thread.Sleep(50);
            } while (Environment.TickCount64 < deadline);
            if (existing != 0) Native.PostMessage(existing, ControlWindow.CommandMessage, args.Contains("--exit") ? -1 : args.Contains("--settings") ? 2 : requestedProfile == "10-man" ? 10 : requestedProfile == "5-man" ? 5 : 0, 0);
            else DarkTheme.Message(null, "The app is already starting. Use its tray icon to switch profiles.", "Notepad++");
            return 0;
        }
        if (args.Contains("--exit")) return 0;
        AppSettings? settings = null;
        try
        {
            if (!File.Exists(AppSettings.FilePath)) AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.default.json")).Save(AppSettings.FilePath);
            settings = AppSettings.Load(AppSettings.FilePath);
            if (requestedProfile != null) { settings.ActiveProfile = requestedProfile; settings.Validate(); settings.Save(AppSettings.FilePath); }
            Application.ThreadException += (_, e) => { Log(e.Exception); DarkTheme.Message(null, e.Exception.Message, settings.DisplayName); };
            using var app = new TrayApp(settings);
            System.Windows.Forms.Timer? initial = null;
            if (!args.Contains("--tray") || args.Contains("--settings"))
            {
                initial = new System.Windows.Forms.Timer { Interval = 100 };
                initial.Tick += (_, _) => { initial.Stop(); if (args.Contains("--settings")) app.ShowSettings(); else app.ShowControls(); initial.Dispose(); }; initial.Start();
            }
            Application.Run(app); return 0;
        }
        catch (Exception ex)
        {
            Log(ex); DarkTheme.Message(null, ex.Message + "\n\nCheck settings.json (a previous save is in settings.json.bak).", settings?.DisplayName ?? "Notepad++");
            return 1;
        }
    }
    internal static string? Option(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException($"{option} needs a value.");
        return args[index + 1];
    }
    internal static void Log(Exception error) => LogLine(error.ToString());
    private static readonly object logGate = new();
    // Called from the UI, focus and hook threads.
    internal static void LogLine(string text)
    {
        try
        {
            lock (logGate)
            {
                var path = Path.Combine(AppContext.BaseDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {text}\n");
            }
        }
        catch { /* logging must not prevent recovery */ }
    }
}
