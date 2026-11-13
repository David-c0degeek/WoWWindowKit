using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WoWWindowKit;

internal sealed class KeyboardHook : IDisposable
{
    private readonly WindowManager manager;
    private readonly Action<int> swap;
    private readonly Action toggleMouse;
    private readonly Native.HookProc callback;
    private readonly HashSet<int> swallowed = [];
    private nint hook;
    internal KeyboardHook(WindowManager manager, Action<int> swap, Action toggleMouse, bool install = true)
    {
        this.manager = manager; this.swap = swap; this.toggleMouse = toggleMouse;
        callback = Callback;
        if (!install) return;
        hook = Native.SetWindowsHookEx(13, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable swap hotkeys.");
    }
    private nint Callback(int code, nint message, nint data)
    {
        if (code >= 0 && ((int)message is 0x100 or 0x101 or 0x104 or 0x105))
        {
            var key = (int)Marshal.PtrToStructure<Native.KeyData>(data).Key;
            var down = (int)message is 0x100 or 0x104;
            var binding = new KeyBinding(key, Native.Down(17), Native.Down(16), Native.Down(18));
            if (HandleKey(binding, down, Native.GetForegroundWindow(), Native.Down(91) || Native.Down(92))) return 1;
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }
    internal bool HandleKey(KeyBinding binding, bool down, nint foreground, bool windowsKey = false)
    {
        if (!down) return swallowed.Remove(binding.Key);
        if (swallowed.Contains(binding.Key)) return true; // suppress repeats and the matching key-up
        if (binding.Key != (int)Keys.F && (binding.Key < (int)Keys.F1 || binding.Key > (int)Keys.F12)) return false;
        if (manager.Editing || windowsKey || !manager.IsManaged(foreground)) return false;
        if (binding == new KeyBinding((int)Keys.F, true, true, false))
        {
            swallowed.Add(binding.Key); toggleMouse(); return true;
        }
        if (manager.Paused) return false;
        for (var i = 0; i < manager.Settings.Current.Slots.Count; i++)
        {
            if (KeyBinding.Parse(manager.Settings.Current.Slots[i].Hotkey) != binding || !manager.Accounts.TryGetValue(i + 1, out var account) || !account.Valid) continue;
            swallowed.Add(binding.Key); swap(i + 1); return true;
        }
        return false;
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); hook = 0; }
}
