using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WoWWindowKit;

// Observe only: never consume, move, or inject mouse input.
internal sealed class MouseHook : IDisposable
{
    private readonly Native.HookProc callback;
    private nint hook;
    internal MouseHook(Action changed)
    {
        callback = (code, message, data) =>
        {
            if (code >= 0 && (int)message is 0x200 or 0x202 or 0x205 or 0x208 or 0x20C) changed();
            return Native.CallNextHookEx(hook, code, message, data);
        };
        hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable mouse focus tracking.");
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); hook = 0; }
}
