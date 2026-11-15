using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WoWWindowKit;

// Observe only: never consume, move, or inject mouse input. The callback receives the
// position Windows is about to apply, before it processes the movement.
internal sealed class MouseHook : IDisposable
{
    private readonly Native.HookProc callback;
    private nint hook;
    internal MouseHook(Action<Native.Point> changed)
    {
        callback = (code, message, data) =>
        {
            if (code >= 0 && (int)message is Native.WmMouseMove or Native.WmLButtonUp or Native.WmRButtonUp or Native.WmMButtonUp or Native.WmXButtonUp) changed(Marshal.PtrToStructure<Native.MouseData>(data).Point);
            return Native.CallNextHookEx(hook, code, message, data);
        };
        hook = Native.SetWindowsHookEx(Native.WhMouseLl, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable mouse focus tracking.");
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); hook = 0; }
}
