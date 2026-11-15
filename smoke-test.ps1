$ErrorActionPreference = 'Stop'
# Copy of Program.InstanceMutexName (C#); test.ps1 checks that the two still match.
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
# The app replaces settings.json atomically; a read can briefly hit the file in use and retries.
function Get-ActiveProfile {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try { return (Get-Content -LiteralPath $settingsPath -Raw -ErrorAction Stop | ConvertFrom-Json).ActiveProfile }
        catch [System.IO.IOException] { Start-Sleep -Milliseconds 50 }
    }
    throw "settings.json stayed locked: $settingsPath"
}
function Save-WindowImage([string]$Name) {
    Start-Sleep -Milliseconds 250
    $bounds = [KitSmokeWindows]::Bounds($running.Id)
    $preview = [Drawing.Bitmap]::new($bounds[2], $bounds[3])
    $graphics = [Drawing.Graphics]::FromImage($preview)
    try {
        $graphics.CopyFromScreen($bounds[0], $bounds[1], 0, 0, $preview.Size)
        $preview.Save((Join-Path $scratch $Name))
    } finally { $graphics.Dispose(); $preview.Dispose() }
}
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
    Wait-Condition { (Test-Path -LiteralPath $settingsPath) -and ((Get-ActiveProfile) -eq '5-man') } 'Packaged app starts and creates settings'
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') } 'Compact controls open automatically on startup'
    Save-WindowImage 'controls-dark.png'
    # A third, custom profile proves that launchers can switch to any profile, not only 5-man/10-man.
    Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -Wait
    if (-not $running.WaitForExit(10000)) { throw 'App did not exit before the custom-profile restart.' }
    $edited = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $edited.Profiles += [pscustomobject]@{ Name = 'Raid'; Slots = $edited.Profiles[0].Slots }
    [IO.File]::WriteAllText($settingsPath, ($edited | ConvertTo-Json -Depth 10))
    $running = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') } 'App restarts with a hand-edited custom profile'
    [KitSmokeWindows]::CloseControls($running.Id)
    Wait-Condition { -not [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') -and -not $running.HasExited } 'Closing controls keeps the tray app running'
    Start-Process -FilePath $exe -ArgumentList '--controls' -WindowStyle Hidden -Wait
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') } 'A second launch reopens existing controls'
    $second = Start-Process -FilePath $exe -ArgumentList '--profile 10-man' -WindowStyle Hidden -PassThru -Wait
    if ($second.ExitCode -ne 0) { throw 'Second instance did not exit cleanly.' }
    Wait-Condition { (Get-ActiveProfile) -eq '10-man' } 'Second shortcut switches the running instance to 10-man'
    [KitSmokeWindows]::CloseControls($running.Id)
    Wait-Condition { -not [KitSmokeWindows]::HasControl($running.Id, 'Catch / assign') } 'Controls hidden before tray-only switches'
    Start-Process -FilePath $exe -ArgumentList '--profile raid --tray' -WindowStyle Hidden -Wait
    Wait-Condition { (Get-ActiveProfile) -eq 'Raid' } 'Launcher switches the running instance to a custom profile'
    Start-Sleep -Milliseconds 500
    if ([KitSmokeWindows]::HasControl($running.Id, 'Catch / assign')) { throw '--tray on a second launch opened the controls.' }
    Write-Output 'PASS: --tray on a second launch switches without opening controls'
    Start-Process -FilePath $exe -ArgumentList '--profile nope --tray' -WindowStyle Hidden -Wait
    Wait-Condition { (Test-Path -LiteralPath (Join-Path $scratch 'app.log')) -and (Get-Content -LiteralPath (Join-Path $scratch 'app.log') -Raw).Contains("Profile 'nope' was not found") } 'Unknown profile name is reported'
    if ((Get-ActiveProfile) -ne 'Raid') { throw 'Unknown profile name changed the active profile.' }
    Write-Output 'PASS: Unknown profile name leaves the active profile unchanged'
    Start-Process -FilePath $exe -ArgumentList '--settings' -WindowStyle Hidden -Wait
    Wait-Condition { [KitSmokeWindows]::HasControl($running.Id, 'App name') } 'Settings command opens the editor separately from controls'
    $renamed = Join-Path $scratch 'notepad++.exe'
    Copy-Item -LiteralPath $exe -Destination $renamed
    Start-Process -FilePath $renamed -ArgumentList '--exit' -WindowStyle Hidden -Wait
    if (-not $running.WaitForExit(10000)) { throw 'Original app did not quit via renamed executable.' }
    Write-Output 'PASS: Single-instance communication works across executable names'
    $running = Start-Process -FilePath $renamed -ArgumentList '--settings' -WindowStyle Hidden -PassThru
    Wait-Condition { [KitSmokeWindows]::Titles($running.Id) -contains 'Notepad++' } 'Renamed executable opens the custom Settings window title'
    Save-WindowImage 'settings-dark.png'
    Start-Process -FilePath $renamed -ArgumentList '--exit' -WindowStyle Hidden -Wait
    if (-not $running.WaitForExit(10000)) { throw 'App did not quit cleanly while Settings was open.' }
    Write-Output 'PASS: Clean exit with Settings open'
    # The only expected log line is the unknown-profile warning above.
    $logPath = Join-Path $scratch 'app.log'
    $unexpected = @(Get-Content -LiteralPath $logPath | Where-Object { $_ -notmatch "Profile 'nope' was not found" })
    if ($unexpected.Count -gt 0) { throw "Unexpected application errors; inspect $logPath" }
    Write-Output 'SUCCESS: Packaged-app smoke tests passed.'
} finally {
    if ($running -and -not $running.HasExited) {
        Start-Process -FilePath $exe -ArgumentList '--exit' -WindowStyle Hidden -Wait
        if (-not $running.WaitForExit(5000)) { Stop-Process -Id $running.Id -Force }
    }
}
