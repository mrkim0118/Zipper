# End-to-end check of Zipper's Explorer commands. Run after building Release:
#   powershell -ExecutionPolicy Bypass -File tests\cli-test.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'src\Zipper\bin\Release\net48\Zipper.exe'
$7z = Join-Path (Split-Path $exe) '7z.exe'
$work = Join-Path $env:TEMP ('zipper-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $work | Out-Null
$fail = 0
function Check($name, $ok) { if ($ok) { "PASS  $name" } else { "FAIL  $name"; $script:fail++ } }
function Run($argList) { $p = Start-Process $exe -ArgumentList $argList -PassThru; if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "timeout: $argList" } }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
function Names($archive) { (& $7z l -ba -slt -sccUTF-8 $archive | Select-String '^Path = ').Line | ForEach-Object { $_.Substring(7) } }

# keep Explorer windows from popping up during the test
$key = 'HKCU:\Software\Zipper'; New-Item $key -Force | Out-Null
$oldOpen = (Get-ItemProperty $key -ErrorAction SilentlyContinue).OpenFolder
Set-ItemProperty $key OpenFolder 0
try {
    $src = Join-Path $work '원본 폴더'; New-Item -ItemType Directory "$src\하위" | Out-Null
    Set-Content "$src\가.txt" '가나다' -Encoding UTF8
    Set-Content "$src\b c.txt" 'space name' -Encoding UTF8
    Set-Content "$src\하위\deep.txt" 'deep' -Encoding UTF8
    $big = New-Object byte[] (3MB); (New-Object Random 1).NextBytes($big); [IO.File]::WriteAllBytes("$src\big.bin", $big)

    # 1. multi-select quick zip: Explorer launches one process per item at once
    $ps = "$src\가.txt", "$src\b c.txt", "$src\하위" | ForEach-Object { Start-Process $exe -ArgumentList '/czip', "`"$_`"" -PassThru }
    $ps | ForEach-Object { $_.WaitForExit(60000) | Out-Null }
    $zip = "$src\원본 폴더.zip"
    Check 'multi-select /czip makes one archive' ((Test-Path $zip) -and -not (Test-Path "$src\가.zip"))
    $n = Names $zip
    Check 'archive holds all three items' (($n -contains '가.txt') -and ($n -contains 'b c.txt') -and ($n -contains '하위\deep.txt'))

    # 2. quick 7z of a folder
    Run '/c7z', "`"$src\하위`""
    Check '/c7z folder' ((Names "$src\하위.7z") -contains '하위\deep.txt')

    # 3. extract variants
    Run '/xfolder', "`"$zip`""
    Check '/xfolder -> new folder' ((Get-Content "$src\원본 폴더\가.txt") -eq '가나다')
    Run '/xsmart', "`"$src\하위.7z`""   # single root folder -> extracted next to the archive
    Check '/xsmart single root goes here' (Test-Path "$src\하위\deep_1.txt")
    $here = Join-Path $work 'here'; New-Item -ItemType Directory $here | Out-Null
    Copy-Item $zip $here
    Run '/xsmart', "`"$here\원본 폴더.zip`""  # several roots -> new folder
    Check '/xsmart several roots -> folder' (Test-Path "$here\원본 폴더\하위\deep.txt")
    Run '/xhere', "`"$here\원본 폴더.zip`""
    Check '/xhere' (Test-Path "$here\가.txt")
    Run '/xhere', "`"$here\원본 폴더.zip`""  # default overwrite mode renames instead of overwriting
    Check 'existing files are kept (auto rename)' (Test-Path "$here\가_1.txt")

    # 4. split archives made elsewhere
    & $7z a -t7z -v1m "$work\split.7z" "$src\big.bin" -bso0 -bsp0
    & $7z a -tzip -v1m "$work\split.zip" "$src\big.bin" -bso0 -bsp0
    Run '/xfolder', "`"$work\split.7z.001`""
    Check 'split .7z.001' ((Get-FileHash "$work\split\big.bin").Hash -eq (Get-FileHash "$src\big.bin").Hash)
    Run '/xfolder', "`"$work\split.zip.001`""
    Check 'split .zip.001' ((Get-FileHash "$work\split\big_1.bin").Hash -eq (Get-FileHash "$src\big.bin").Hash)

    # 5. tar.gz name handling
    & $7z a -ttar "$work\t.tar" "$src\가.txt" -bso0 -bsp0; & $7z a -tgzip "$work\t.tar.gz" "$work\t.tar" -bso0 -bsp0
    Run '/xfolder', "`"$work\t.tar.gz`""
    Check '.tar.gz -> folder "t"' (Test-Path "$work\t\t.tar")

    # 6. ALZ / EGG (samples from the EggDotNet test suite, MIT)
    Copy-Item "$root\tests\data\*" $work
    Run '/xfolder', "`"$work\defaults.alz`""
    Check 'alz: 6 files' ((Get-ChildItem "$work\defaults" -Recurse -File).Count -eq 6)
    Check 'alz: content matches' ((Get-FileHash "$work\defaults\lorem_ipsum_short.txt").Hash -eq 'ACFE59AFCFFCBE68A4DEE454D11F5BF78DFDEE32C77A03C1A50CF384418FE38F')
    Run '/xsmart', "`"$work\basic_deflate_folders.egg`""
    Check 'egg (deflate, folders): 3 files' ((Get-ChildItem "$work\basic_deflate_folders" -Recurse -File).Count -eq 3)
    Run '/xfolder', "`"$work\lzma_simple.egg`""
    Check 'egg (lzma)' ((Get-Item "$work\lzma_simple\lorem_ipsum.txt").Length -eq 34446)
}
finally {
    if ($null -eq $oldOpen) { Remove-ItemProperty $key OpenFolder } else { Set-ItemProperty $key OpenFolder $oldOpen }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { "$fail test(s) failed"; exit 1 } else { 'all passed' }
