# Builds Release and runs the in-process integration suite.
# -Groups runs a comma-separated subset (for example 'settings,tracking-marker'); see SelfTest.cs.
param([string[]]$Groups)
$ErrorActionPreference = 'Stop'

# smoke-test.ps1 keeps a PowerShell copy of the single-instance mutex name; both test scripts
# rely on it to avoid touching a live session, so a rename must update both.
$programSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'src\WoWWindowKit\Program.cs') -Raw
$smokeSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'smoke-test.ps1') -Raw
$instanceId = [regex]::Match($programSource, 'InstanceId = "([0-9A-Za-z]+)"').Groups[1].Value
if (-not $instanceId -or -not $programSource.Contains('InstanceMutexName = @"Local\WoWWindowKit." + InstanceId') -or -not $smokeSource.Contains("'Local\WoWWindowKit.$instanceId'")) {
    throw 'smoke-test.ps1 and Program.InstanceMutexName disagree on the single-instance mutex name.'
}

$project = Join-Path $PSScriptRoot 'src\WoWWindowKit\WoWWindowKit.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$binary = Join-Path $PSScriptRoot 'src\WoWWindowKit\bin\Release\net10.0-windows\WoWWindowKit.exe'
$arguments = @('--self-test')
if ($Groups) { $arguments += ($Groups -join ',') }
$process = Start-Process -FilePath $binary -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path (Split-Path $binary) 'self-test-results.txt')
if ($process.ExitCode -ne 0) { throw "Integration tests failed ($($process.ExitCode))." }
