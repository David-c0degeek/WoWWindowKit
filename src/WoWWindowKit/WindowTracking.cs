namespace WoWWindowKit;

/// <summary>
/// Controls Windows' own active window tracking ("activate the window under the mouse"),
/// the mechanism WowOpenBox relied on. Windows performs that activation inside its input
/// processing, so every key that follows the mouse movement already reaches the new window.
/// The app turns it on only while the pointer is over a managed WoW window.
/// Changes are never written to the user profile (no SPIF_UPDATEINIFILE), so a logoff or
/// reboot always restores the user's own setting. A marker file holds the original values
/// while tracking is on, so a crash is repaired on the next start.
/// </summary>
internal sealed class WindowTracking : IDisposable
{
    internal readonly record struct Values(int Tracking, int ZOrder, int Timeout);

    private readonly string marker;
    private readonly Action<Values> apply;
    private readonly object gate = new();
    private readonly Values original;
    private bool on, markerWritten, disposed;

    internal WindowTracking(string marker) : this(marker, ReadSystem, ApplySystem) { }

    internal WindowTracking(string marker, Func<Values> read, Action<Values> apply)
    {
        this.marker = marker; this.apply = apply;
        // A previous run that ended while tracking was on left its original values behind.
        if (TryReadMarker(out var left)) { apply(left); TryDelete(); }
        original = read();
    }

    internal Values Original => original;
    internal bool Enabled { get { lock (gate) return on; } }

    /// <summary>Called on the input-hook thread before Windows handles the mouse movement.</summary>
    internal void Set(bool enable)
    {
        lock (gate)
        {
            if (disposed || enable == on) return;
            if (enable && !markerWritten)
            {
                try { File.WriteAllText(marker, $"{original.Tracking} {original.ZOrder} {original.Timeout}"); markerWritten = true; }
                catch (Exception ex) { Program.Log(ex); }
            }
            // Activate immediately and never raise the window above others.
            apply(enable ? new Values(1, 0, 0) : original);
            on = enable;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (on) apply(original);
            on = false;
            if (markerWritten) TryDelete();
        }
    }

    private bool TryReadMarker(out Values values)
    {
        values = default;
        try
        {
            if (!File.Exists(marker)) return false;
            var parts = File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
            if (parts.Length != 3) { TryDelete(); return false; }
            values = new(parts[0], parts[1], parts[2]);
            return true;
        }
        catch (Exception ex) { Program.Log(ex); TryDelete(); return false; }
    }

    private void TryDelete() { try { File.Delete(marker); } catch { /* retried on the next start */ } }

    private const uint GetTracking = 0x1000, SetTracking = 0x1001, GetZOrder = 0x100C, SetZOrder = 0x100D, GetTimeout = 0x2002, SetTimeout = 0x2003;

    private static Values ReadSystem()
    {
        Native.SystemParametersInfo(GetTracking, 0, out int tracking, 0);
        Native.SystemParametersInfo(GetZOrder, 0, out int zOrder, 0);
        Native.SystemParametersInfo(GetTimeout, 0, out int timeout, 0);
        return new(tracking != 0 ? 1 : 0, zOrder != 0 ? 1 : 0, timeout);
    }

    private static void ApplySystem(Values values)
    {
        // These set-actions take the value itself in pvParam. winIni 0: memory only, no broadcast.
        if (values.Tracking == 0) Native.SystemParametersInfo(SetTracking, 0, 0, 0);
        Native.SystemParametersInfo(SetZOrder, 0, values.ZOrder, 0);
        Native.SystemParametersInfo(SetTimeout, 0, values.Timeout, 0);
        if (values.Tracking != 0) Native.SystemParametersInfo(SetTracking, 0, 1, 0);
    }
}
