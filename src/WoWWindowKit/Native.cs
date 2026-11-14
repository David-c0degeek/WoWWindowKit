using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WoWWindowKit;

internal static class Native
{
    internal const int GwlStyle = -16, WmHotkey = 0x0312;
    internal const long BorderMask = 0x00CF0000; // caption, frame, system menu, min/max boxes
    internal const uint NoActivate = 0x10, NoZOrder = 0x4, FrameChanged = 0x20;
    internal delegate bool EnumProc(nint window, nint data);
    internal delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    [StructLayout(LayoutKind.Sequential)] internal struct Placement
    {
        public int Length, Flags, ShowCmd;
        public Point Min, Max;
        public Rect Normal;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyData { public uint Key, ScanCode, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public Point Point; public uint MouseInfo, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Window; public uint Id; public nint WParam, LParam; public uint Time; public Point Point; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] internal static extern nint SetActiveWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] internal static extern bool IsHungAppWindow(nint window);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] internal static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] internal static extern bool GetProcessTimes(nint process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref int size);
    [DllImport("user32.dll")] internal static extern bool SystemParametersInfo(uint action, uint param, nint value, uint winIni);
    [DllImport("user32.dll")] internal static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint thread, uint message, nint wp, nint lp);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] internal static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] internal static extern bool SetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint window, uint message, nint wp, nint lp);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint window, uint message, nint wp, nint lp);
    internal static string Title(nint window) { var text = new StringBuilder(1024); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    internal static bool Down(int key) => GetAsyncKeyState(key) < 0;
    internal static nint RootAt(Point point) => GetAncestor(WindowFromPoint(point), 2);
    // Serializes activation between the focus worker and UI-thread swaps; both may attach input queues.
    private static readonly object activationGate = new();
    internal static bool Activate(nint target)
    {
        lock (activationGate)
        {
            if (GetForegroundWindow() == target) return true;
            if (SetForegroundWindow(target)) return true;
            var foreground = GetForegroundWindow();
            if (foreground == 0 || IsHungAppWindow(foreground) || IsHungAppWindow(target)) return false;
            var foregroundThread = GetWindowThreadProcessId(foreground, out _);
            var targetThread = GetWindowThreadProcessId(target, out _);
            var thisThread = GetCurrentThreadId();
            // Sharing only the current foreground queue grants foreground rights. It leaves the
            // target's input state alone and avoids synchronous calls into its busy render thread.
            if (thisThread != foregroundThread && AttachThreadInput(thisThread, foregroundThread, true))
            {
                try { if (SetForegroundWindow(target) || GetForegroundWindow() == target) return true; }
                finally { AttachThreadInput(thisThread, foregroundThread, false); }
            }
            var attachedForeground = thisThread != foregroundThread && AttachThreadInput(thisThread, foregroundThread, true);
            var attachedTarget = targetThread != thisThread && targetThread != foregroundThread && AttachThreadInput(thisThread, targetThread, true);
            try { BringWindowToTop(target); SetActiveWindow(target); return SetForegroundWindow(target); }
            finally
            {
                if (attachedTarget) AttachThreadInput(thisThread, targetThread, false);
                if (attachedForeground) AttachThreadInput(thisThread, foregroundThread, false);
            }
        }
    }
    /// <summary>Executable name without extension, or null when the process cannot be queried.</summary>
    internal static string? ProcessName(nint process)
    {
        var text = new StringBuilder(1024); var size = text.Capacity;
        return QueryFullProcessImageName(process, 0, text, ref size) ? Path.GetFileNameWithoutExtension(text.ToString(0, size)) : null;
    }
    internal static void Position(nint window, Rectangle bounds, bool frameChanged = true, bool asynchronous = false)
    {
        var flags = NoActivate | NoZOrder | (frameChanged ? FrameChanged : 0u);
        // Same async/deferred-erase/no-changing flags as WowOpenBox, plus no activation.
        if (asynchronous) flags |= 0x6600;
        if (!SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not reposition a game window. If WoW runs as administrator, run this app at the same level.");
    }
}
