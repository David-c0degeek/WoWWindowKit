$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\WoWWindowKit\WoWWindowKit.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$binary = Join-Path $PSScriptRoot 'src\WoWWindowKit\bin\Release\net10.0-windows\WoWWindowKit.exe'
$process = Start-Process -FilePath $binary -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
Get-Content -LiteralPath (Join-Path (Split-Path $binary) 'self-test-results.txt')
if ($process.ExitCode -ne 0) { throw "Integration tests failed ($($process.ExitCode))." }
