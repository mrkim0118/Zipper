# Builds Zipper (Release) and the installer: Installer\Output\ZipperSetup-<version>.exe
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build src\Zipper -c Release -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }
& $iscc /Q Installer\Zipper.iss
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Get-Item Installer\Output\ZipperSetup-*.exe
