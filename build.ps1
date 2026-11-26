# Builds Zipper (Release), signs it, and builds the signed installer: Installer\Output\ZipperSetup-<version>.exe
#   -NoSign  skip code signing (no certificate needed)
param([switch]$NoSign)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
dotnet build src\Zipper -c Release -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$sign = Join-Path $PSScriptRoot 'Installer\sign.ps1'
if (-not $NoSign) {
    powershell -NoProfile -ExecutionPolicy Bypass -File $sign 'src\Zipper\bin\Release\net48\Zipper.exe'
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
}
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }
# Inno Setup signs the setup and its uninstaller through this tool ($f = file, $q = quote)
$isccArgs = if ($NoSign) { @() } else { @('/DSign', "/Szippersign=powershell.exe -NoProfile -ExecutionPolicy Bypass -File `$q$sign`$q `$f") }
& $iscc /Q @isccArgs Installer\Zipper.iss
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Get-Item Installer\Output\ZipperSetup-*.exe
