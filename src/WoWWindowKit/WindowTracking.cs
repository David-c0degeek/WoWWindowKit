using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WoWWindowKit;

/// <summary>
/// Controls Windows' own active window tracking ("activate the window under the mouse"),
/// the mechanism WowOpenBox relied on. Windows performs that activation inside its input
/// processing, so every key that follows the mouse movement already reaches the new window.
/// The app turns it on only while the pointer is over a managed WoW window.
/// Changes are never written to the user profile (no SPIF_UPDATEINIFILE), so a logoff or
/// reboot always restores the user's own setting. The constructor (UI thread, startup)
/// records the original values in a marker file before tracking can ever be enabled, so a
/// crash is repaired on the next start. If that is not possible, tracking stays unavailable
/// for the run and the app activates hovered windows itself.
/// </summary>
internal sealed class WindowTracking : IDisposable
{
    internal readonly record struct Values(int Tracking, int ZOrder, int Timeout);

    private readonly string marker;
    private readonly Action<Values> apply;
    private readonly object gate = new();
    private readonly Values original;
    // on: tracking values applied by Set(true). dirty: the system may differ from the originals.
    // failed: a system call failed; Set refuses further changes for the rest of the run.
    private bool on, dirty, failed, disposed;

    internal WindowTracking(string marker) : this(marker, ReadSystem, ApplySystem) { }

    internal WindowTracking(string marker, Func<Values> read, Action<Values> apply)
    {
        this.marker = marker; this.apply = apply;
        try
        {
            // A previous run that ended without restoring left its original values behind.
            // If re-applying them fails, the marker stays for the next start.
            if (TryReadMarker(out var left)) { apply(left); File.Delete(marker); }
            original = read();
            File.WriteAllText(marker, $"{original.Tracking} {original.ZOrder} {original.Timeout}");
        }
        catch (Exception ex)
        {
            Unavailable = ex.Message;
            failed = true;
        }
    }

    /// <summary>Why tracking cannot be used this run; null when it is available.</summary>
    internal string? Unavailable { get; }
    /// <summary>
    /// Raised once, on the calling (hook) thread, when a system call fails after startup.
    /// The owner must marshal any logging or UI work; the hook thread must not do I/O.
    /// </summary>
    internal Action<Exception>? Failed;
    internal Values Original => original;
    internal bool Enabled { get { lock (gate) return on; } }

    /// <summary>
    /// Called on the input-hook thread before Windows handles the mouse movement. Performs
    /// only the system calls, never file I/O. Returns false when the change was not made.
    /// </summary>
    internal bool Set(bool enable)
    {
        lock (gate)
        {
            if (disposed || failed) return false;
            if (enable == on) return true;
            try
            {
                // Activate immediately and never raise the window above others.
                dirty = true;
                apply(enable ? new Values(1, 0, 0) : original);
                on = enable;
                dirty = enable;
                return true;
            }
            catch (Exception ex)
            {
                failed = true; on = false;
                // Best effort; if this fails too, Dispose retries and the marker stays.
                try { apply(original); dirty = false; } catch { /* dirty stays set */ }
                Failed?.Invoke(ex);
                return false;
            }
        }
    }

    /// <summary>True when the system was left changed and the marker is kept for the next start.</summary>
    internal bool RestorePending { get { lock (gate) return dirty; } }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            on = false;
            if (Unavailable != null) return; // the marker, if any, belongs to an unrepaired earlier run
            if (dirty)
            {
                try { apply(original); dirty = false; }
                catch (Exception ex) { Program.Log(ex); }
            }
            // Delete the recovery evidence only once the originals are known to be in place.
            if (!dirty) { try { File.Delete(marker); } catch { /* the next start re-applies the same originals */ } }
        }
    }

    private bool TryReadMarker(out Values values)
    {
        values = default;
        if (!File.Exists(marker)) return false;
        var parts = File.ReadAllText(marker).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && int.TryParse(parts[0], out var tracking) && int.TryParse(parts[1], out var zOrder) && int.TryParse(parts[2], out var timeout))
        {
            values = new(tracking, zOrder, timeout);
            return true;
        }
        // Unreadable content cannot be repaired; it is replaced by this run's marker.
        Program.LogLine($"Ignored malformed {Path.GetFileName(marker)}.");
        return false;
    }

    private const uint GetTracking = 0x1000, SetTracking = 0x1001, GetZOrder = 0x100C, SetZOrder = 0x100D, GetTimeout = 0x2002, SetTimeout = 0x2003;

    private static Values ReadSystem()
    {
        if (!Native.SystemParametersInfo(GetTracking, 0, out int tracking, 0)
            || !Native.SystemParametersInfo(GetZOrder, 0, out int zOrder, 0)
            || !Native.SystemParametersInfo(GetTimeout, 0, out int timeout, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read Windows' active window tracking settings.");
        return new(tracking != 0 ? 1 : 0, zOrder != 0 ? 1 : 0, timeout);
    }

    private static void ApplySystem(Values values)
    {
        // These set-actions take the value itself in pvParam. winIni 0: memory only, no broadcast.
        if (values.Tracking == 0) Check(Native.SystemParametersInfo(SetTracking, 0, 0, 0));
        Check(Native.SystemParametersInfo(SetZOrder, 0, values.ZOrder, 0));
        Check(Native.SystemParametersInfo(SetTimeout, 0, values.Timeout, 0));
        if (values.Tracking != 0) Check(Native.SystemParametersInfo(SetTracking, 0, 1, 0));
        static void Check(bool ok)
        {
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not change Windows' active window tracking settings.");
        }
    }
}
