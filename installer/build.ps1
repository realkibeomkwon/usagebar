# Builds installer\Output\UsageBar-<version>-setup.exe. Needs Inno Setup 6 (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'
$publish = Join-Path $PSScriptRoot 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Self-contained, so the PC needs no .NET Desktop Runtime.
dotnet publish (Join-Path $PSScriptRoot '..\UsageBar.csproj') -c Release -r win-x64 --self-contained -o $publish
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }
& $iscc (Join-Path $PSScriptRoot 'UsageBar.iss')
exit $LASTEXITCODE
