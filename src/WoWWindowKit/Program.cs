using System.Runtime.InteropServices;

namespace WoWWindowKit;

/// <summary>What a launch asks the (first or already running) instance to do.</summary>
internal enum ControlCommand { ShowControls = 1, ShowSettings = 2, Exit = 3, Tray = 4 }

/// <summary>Parsed command line. Unrecognized arguments are ignored, as before.</summary>
internal sealed record LaunchOptions(ControlCommand Command, string? Profile)
{
    internal static LaunchOptions Parse(string[] args)
    {
        var profile = Program.Option(args, "--profile");
        var command = args.Contains("--exit") ? ControlCommand.Exit
            : args.Contains("--settings") ? ControlCommand.ShowSettings
            : args.Contains("--controls") ? ControlCommand.ShowControls
            : args.Contains("--tray") ? ControlCommand.Tray
            : ControlCommand.ShowControls; // the default; Controls.cmd and shortcuts pass --controls
        return new(command, profile);
    }
}

internal static class Program
{
    // The suffix ties the mutex and the IPC window to this application. smoke-test.ps1 keeps a
    // PowerShell copy of InstanceMutexName; test.ps1 checks that the two still match.
    private const string InstanceId = "1C825122";
    internal const string InstanceMutexName = @"Local\WoWWindowKit." + InstanceId;
    internal const string ControlWindowName = "WoWWindowKit.Control." + InstanceId;

    internal static Icon AppIcon { get; } = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // Deliberately opt into WinForms' explicit dark mode, independent of the OS preference.
#pragma warning disable WFO5001
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        if (args.Contains("--test-client"))
        {
            var number = int.Parse(Option(args, "--test-client")!, System.Globalization.CultureInfo.InvariantCulture);
            using var client = new TestClientForm { Text = $"WoW {number}", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(80 + number * 8, 80 + number * 8, 280, 180) };
            Application.Run(client);
            return 0;
        }
        if (args.Contains("--self-test"))
        {
            var index = Array.IndexOf(args, "--self-test");
            return SelfTest.Run(index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null);
        }
        LaunchOptions options;
        try { options = LaunchOptions.Parse(args); }
        catch (ArgumentException ex) { DarkTheme.Message(null, ex.Message, AppSettings.DefaultDisplayName); return 1; }
        using var mutex = new Mutex(true, InstanceMutexName, out var first);
        if (!first) return SendToRunningInstance(options);
        if (options.Command == ControlCommand.Exit) return 0;

        AppSettings settings;
        try
        {
            if (!File.Exists(AppSettings.FilePath)) AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.default.json")).Save(AppSettings.FilePath);
            settings = AppSettings.Load(AppSettings.FilePath);
            if (options.Profile != null)
            {
                settings.ActiveProfile = settings.FindProfile(options.Profile)
                    ?? throw new InvalidDataException($"Profile '{options.Profile}' was not found in settings.json.");
                settings.Save(AppSettings.FilePath);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            DarkTheme.Message(null, ex.Message + "\n\nCheck settings.json (a previous save is in settings.json.bak).", AppSettings.DefaultDisplayName);
            return 1;
        }

        try
        {
            Application.ThreadException += (_, e) => { Log(e.Exception); DarkTheme.Message(null, e.Exception.Message, settings.DisplayName); };
            using var app = new TrayApp(settings);
            System.Windows.Forms.Timer? initial = null;
            if (options.Command is ControlCommand.ShowControls or ControlCommand.ShowSettings)
            {
                initial = new System.Windows.Forms.Timer { Interval = 100 };
                initial.Tick += (_, _) =>
                {
                    initial.Stop();
                    if (options.Command == ControlCommand.ShowSettings) app.ShowSettings();
                    else app.ShowControls();
                    initial.Dispose();
                };
                initial.Start();
            }
            Application.Run(app);
            return 0;
        }
        catch (Exception ex)
        {
            // Not a settings problem: hooks, windows or other startup work failed.
            Log(ex);
            DarkTheme.Message(null, ex.Message + "\n\nDetails are in app.log beside the executable.", settings.DisplayName);
            return 1;
        }
    }

    /// <summary>Hands the launch to the running instance: WM_COPYDATA carries the command and the profile name.</summary>
    private static int SendToRunningInstance(LaunchOptions options)
    {
        nint existing;
        var deadline = Environment.TickCount64 + 5000;
        do
        {
            existing = Native.FindWindow(null, ControlWindowName);
            if (existing != 0) break;
            Thread.Sleep(50);
        } while (Environment.TickCount64 < deadline);
        if (existing == 0)
        {
            DarkTheme.Message(null, "The app is already starting. Use its tray icon to switch profiles.", AppSettings.DefaultDisplayName);
            return 0;
        }
        var text = options.Profile ?? "";
        var buffer = Marshal.StringToHGlobalUni(text);
        try
        {
            var data = new Native.CopyData { Data = (nint)options.Command, Size = text.Length * sizeof(char), Pointer = buffer };
            // Success needs both delivery and the receiver's acceptance (it returns 1).
            if (Native.SendMessageTimeout(existing, Native.WmCopyData, 0, ref data, Native.SmtoAbortIfHung, 5000, out var accepted) != 0 && accepted == 1) return 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
        DarkTheme.Message(null, "The running app did not respond. Use its tray icon, or exit it and try again.", AppSettings.DefaultDisplayName);
        return 1;
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
    // Called from the UI and focus threads, never from a hook callback (file I/O).
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
