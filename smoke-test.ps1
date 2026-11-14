$ErrorActionPreference = 'Stop'
$existingApp = $null
if ([Threading.Mutex]::TryOpenExisting('Local\WoWWindowKit.1C825122', [ref]$existingApp)) {
    $existingApp.Dispose()
    throw 'Exit the running app before smoke testing; tests must not switch or stop your live session.'
}
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Collections.Generic;
public static class KitSmokeWindows {
    private delegate bool Callback(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, Callback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    public static bool HasControl(int pid, string label) {
        bool found = false;
        EnumWindows((window, state) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == pid && IsWindowVisible(window)) EnumChildWindows(window, (child, unused) => {
                var text = new StringBuilder(1024); GetWindowText(child, text, 1024);
                if (text.ToString() == label && IsWindowVisible(child)) found = true;
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return found;
    }
    public static void CloseControls(int pid) {
        EnumWindows((window, state) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == pid && IsWindowVisible(window)) PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }
    public static int[] Bounds(int pid) {
        int[] result = null;
        EnumWindows((window, state) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == pid && IsWindowVisible(window)) {
                var text = new StringBuilder(1024); GetWindowText(window, text, 1024);
                Rect rect;
                if (text.ToString() == "Notepad++" && GetWindowRect(window, out rect)) result = new[] { rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top };
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static string[] Titles(int pid, bool visibleOnly = true) {
        var titles = new List<string>();
        EnumWindows((window, state) => {
            uint owner; GetWindowThreadProcessId(window, out owner);
            if (owner == pid && (!visibleOnly || IsWindowVisible(window))) {
                var text = new StringBuilder(1024); GetWindowText(window, text, 1024); titles.Add(text.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
}
'@
$root = Join-Path $PSScriptRoot 'artifacts'
$scratch = Join-Path $root ('smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\WoWWindowKit.exe'), (Join-Path $PSScriptRoot 'dist\settings.default.json') -Destination $scratch
$exe = Join-Path $scratch 'WoWWindowKit.exe'
$settingsPath = Join-Path $scratch 'settings.json'
$running = $null
function Wait-Condition([scriptblock]$Condition, [string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if (& $Condition) { Write-Output "PASS: $Name"; return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Name; windows=$([KitSmokeWindows]::Titles($running.Id, $false) -join ', '); exited=$($running.HasExited)"
}
try {
    $running = Start-Process -FilePath $exe -ArgumentList '--profile 5-man' -WindowStyle Hidden -PassThru
    Wait-Condition { (Test-Path -LiteralPath $settingsPath) -and ((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).ActiveProfile -eq '5-man') } 'Packaged app starts and creates settings'
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') } 'Compact controls open automatically on startup'
    Start-Sleep -Milliseconds 250
    $bounds = [KitSmokeWindows]::Bounds($running.Id)
    $preview = [Drawing.Bitmap]::new($bounds[2], $bounds[3])
    $graphics = [Drawing.Graphics]::FromImage($preview)
    try {
        $graphics.CopyFromScreen($bounds[0], $bounds[1], 0, 0, $preview.Size)
        $preview.Save((Join-Path $scratch 'controls-dark.png'))
    } finally { $graphics.Dispose(); $preview.Dispose() }
    [KitSmokeWindows]::CloseControls($running.Id)
    Wait-Condition { -not [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') -and -not $running.HasExited } 'Closing controls keeps the tray app running'
    $reopen = Start-Process -FilePath $exe -ArgumentList '--controls' -WindowStyle Hidden -PassThru -Wait
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') } 'A second launch reopens existing controls'
    $second = Start-Process -FilePath $exe -ArgumentList '--profile 10-man' -WindowStyle Hidden -PassThru -Wait
    if ($second.ExitCode -ne 0) { throw 'Second instance did not exit cleanly.' }
    Wait-Condition { (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).ActiveProfile -eq '10-man' } 'Second shortcut switches the running instance to 10-man'
    $openSettings = Start-Process -FilePath $exe -ArgumentList '--settings' -WindowStyle Hidden -PassThru -Wait
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'App name') } 'Settings command opens the editor separately from controls'
    $renamed = Join-Path $scratch 'notepad++.exe'
    Copy-Item -LiteralPath $exe -Destination $renamed
    $quit = Start-Process -FilePath $renamed -ArgumentList '--exit' -WindowStyle Hidden -PassThru -Wait
    if (-not $running.WaitForExit(10000)) { throw 'Original app did not quit via renamed executable.' }
    Write-Output 'PASS: Single-instance communication works across executable names'
    $running = Start-Process -FilePath $renamed -ArgumentList '--settings' -WindowStyle Hidden -PassThru
    Wait-Condition { [KitSmokeWindows]::Titles($running.Id) -contains 'Notepad++' } 'Renamed executable opens the custom Settings window title'
    Start-Sleep -Milliseconds 250
    $bounds = [KitSmokeWindows]::Bounds($running.Id)
    $preview = [Drawing.Bitmap]::new($bounds[2], $bounds[3])
    $graphics = [Drawing.Graphics]::FromImage($preview)
    try {
        $graphics.CopyFromScreen($bounds[0], $bounds[1], 0, 0, $preview.Size)
        $preview.Save((Join-Path $scratch 'settings-dark.png'))
    } finally { $graphics.Dispose(); $preview.Dispose() }
    $quit = Start-Process -FilePath $renamed -ArgumentList '--exit' -WindowStyle Hidden -PassThru -Wait
    if (-not $running.WaitForExit(10000)) { throw 'App did not quit cleanly while Settings was open.' }
    Write-Output 'PASS: Clean exit with Settings open'
    if (Test-Path -LiteralPath (Join-Path $scratch 'app.log')) { throw 'Unexpected application errors; inspect artifacts smoke folder.' }
    Write-Output 'SUCCESS: Packaged-app smoke tests passed.'
} finally {
    if ($running -and -not $running.HasExited) {
        $quit = Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -PassThru -Wait
        if (-not $running.WaitForExit(5000)) { Stop-Process -Id $running.Id -Force }
    }
}
