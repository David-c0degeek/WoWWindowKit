<#
.SYNOPSIS
Adds WoWWindowKit to Start and the desktop, ready to pin to the taskbar.
.DESCRIPTION
Run again after moving the application. Shortcuts launch the executable directly
and keep the last selected profile. No administrator access is needed.
#>
param(
    [switch]$NoDesktop,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'

$places = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktop) { $places += [Environment]::GetFolderPath('Desktop') }
$name = 'WoWWindowKit.lnk'

if ($Remove) {
    foreach ($place in $places) {
        $path = Join-Path $place $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path
            Write-Host "Removed $path"
        }
    }
    return
}

$appRoot = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'WoWWindowKit.exe'))) {
    $appRoot = Join-Path $PSScriptRoot 'dist'
}
$exe = Join-Path $appRoot 'WoWWindowKit.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw 'Build the application first with .\build.ps1 -Portable, then run Install shortcut.cmd again.'
}

$shell = New-Object -ComObject WScript.Shell
foreach ($place in $places) {
    $path = Join-Path $place $name
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = '--controls'
    $shortcut.WorkingDirectory = $appRoot
    $shortcut.IconLocation = "$exe,0"
    $shortcut.Description = 'Open WoWWindowKit controls using the last selected profile.'
    $shortcut.Save()
    Write-Host "Created $path"
}
Write-Host ''
Write-Host 'To pin it: find WoWWindowKit in Start, right-click it, then choose Pin to taskbar.'
Write-Host 'The desktop shortcut also works (look under Show more options if needed).'
