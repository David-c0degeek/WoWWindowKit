using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// user32, kernel32 and dwmapi are system DLLs: never resolve them from the application folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace WoWWindowKit;

internal static class Native
{
    internal const int GwlStyle = -16;
    internal const long BorderMask = 0x00CF0000; // caption, frame, system menu, min/max boxes
    internal const long WsVisible = 0x10000000;

    // SetWindowPos flags and insert-after handles.
    internal const uint SwpNoSize = 0x1, SwpNoMove = 0x2, NoZOrder = 0x4, NoActivate = 0x10, FrameChanged = 0x20,
        SwpNoOwnerZOrder = 0x200, SwpNoSendChanging = 0x400, SwpDeferErase = 0x2000, SwpAsyncWindowPos = 0x4000;
    internal const nint HwndTopMost = -1, HwndNoTopMost = -2;

    // ShowWindow commands.
    internal const int SwShowNormal = 1, SwShowMinimized = 2, SwShowMaximized = 3, SwShowNoActivate = 4, SwShow = 5, SwShowMinNoActive = 7, SwShowNa = 8;

    internal const uint GwOwner = 4, GaRoot = 2;
    internal const int WhKeyboardLl = 13, WhMouseLl = 14;
    internal const uint WmCopyData = 0x4A, WmQuit = 0x12, WmWindowPosChanged = 0x47, WmStyleChanged = 0x7D;
    internal const int WmKeyDown = 0x100, WmKeyUp = 0x101, WmSysKeyDown = 0x104, WmSysKeyUp = 0x105;
    internal const int WmMouseMove = 0x200, WmLButtonUp = 0x202, WmRButtonUp = 0x205, WmMButtonUp = 0x208, WmXButtonUp = 0x20C;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint SmtoAbortIfHung = 0x2;

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
    [StructLayout(LayoutKind.Sequential)] internal struct CopyData { public nint Data; public int Size; public nint Pointer; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(nint window);
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
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SystemParametersInfo(uint action, uint param, nint value, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll")] internal static extern nint SendMessage(nint window, uint message, nint wp, nint lp);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static extern nint SendMessageTimeout(nint window, uint message, nint wp, ref CopyData lp, uint flags, uint timeout, out nint result);
    internal static string Title(nint window) { var text = new StringBuilder(1024); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    internal static bool Down(Keys key) => GetAsyncKeyState((int)key) < 0;
    internal static nint RootAt(Point point) => GetAncestor(WindowFromPoint(point), GaRoot);
    // Activation runs only on the focus worker in production (UI-thread requests are queued
    // to it). The lock keeps the whole attach/activate/detach sequence serialized for any
    // other caller, such as the self-test thread.
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
        // Same asynchronous / deferred-erase / no-owner-Z-order / no-WM_WINDOWPOSCHANGING flags
        // as WowOpenBox (0x6600), plus no activation. Whether skipping WM_WINDOWPOSCHANGING
        // affects the game's swap-chain resize is an open question (docs/implementation.md).
        if (asynchronous) flags |= SwpAsyncWindowPos | SwpDeferErase | SwpNoOwnerZOrder | SwpNoSendChanging;
        if (!SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not reposition a game window. If WoW runs as administrator, run this app at the same level.");
    }
}
