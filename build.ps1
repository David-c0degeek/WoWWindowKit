param([switch]$Portable, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\WoWWindowKit\WoWWindowKit.csproj'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'dist' }
$arguments = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '-o', $output, '--nologo', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
if ($Portable) {
    $arguments += @('--self-contained', 'true', '-p:EnableCompressionInSingleFile=true')
} else {
    $arguments += @('--self-contained', 'false')
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install shortcut.cmd'), (Join-Path $PSScriptRoot 'install-shortcut.ps1') -Destination $output
foreach ($profileName in @('5-man','10-man')) {
    $lines = @('@echo off', 'cd /d "%~dp0"', "start `"`" `"%~dp0WoWWindowKit.exe`" --profile $profileName")
    [IO.File]::WriteAllLines((Join-Path $output "Start $profileName.cmd"), $lines, [Text.Encoding]::ASCII)
}
[IO.File]::WriteAllLines((Join-Path $output 'Settings.cmd'), @('@echo off', 'cd /d "%~dp0"', 'start "" "%~dp0WoWWindowKit.exe" --settings'), [Text.Encoding]::ASCII)
[IO.File]::WriteAllLines((Join-Path $output 'Controls.cmd'), @('@echo off', 'cd /d "%~dp0"', 'start "" "%~dp0WoWWindowKit.exe" --controls'), [Text.Encoding]::ASCII)
Write-Host "Built in $output"
