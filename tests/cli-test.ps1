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
$oldExe = (Get-ItemProperty $key -ErrorAction SilentlyContinue).RenameExe
Set-ItemProperty $key OpenFolder 0
Set-ItemProperty $key RenameExe 1
try {
    $src = Join-Path $work '원본 폴더'; New-Item -ItemType Directory "$src\하위" | Out-Null
    Set-Content "$src\가.txt" '가나다' -Encoding UTF8
    Set-Content "$src\b c.txt" 'space name' -Encoding UTF8
    Set-Content "$src\하위\deep.txt" 'deep' -Encoding UTF8
    $big = New-Object byte[] (3MB); (New-Object Random 1).NextBytes($big); [IO.File]::WriteAllBytes("$src\big.bin", $big)

    # With the option on, .exe file entries are stored as .zipper (folders keep their names),
    # plus a batch file that renames them back for people without Zipper.
    $nestedDir = New-Item -ItemType Directory "$src\하위\tools.exe"
    Set-Content "$nestedDir\app.v2.ExE" 'nested executable fixture'
    Set-Content "$src\run.EXE" 'top-level executable fixture'
    Run '/czip', "`"$src\run.EXE`""
    $single = Names "$src\run.zip"
    Check 'ZIP stores .EXE as .zipper' (($single -contains 'run.zipper') -and ($single -notcontains 'run.EXE'))
    Check 'restore batch file is added' ($single -contains 'EXE 복원하기.bat')
    Check 'original EXE is preserved' ((Get-Content "$src\run.EXE") -eq 'top-level executable fixture')
    # someone without Zipper: plain 7-Zip extract, then the batch file
    $plain = "$work\plain"; & $7z x "$src\run.zip" "-o$plain" -bso0 -bsp0
    cmd /c "echo.| `"$plain\EXE 복원하기.bat`"" | Out-Null
    Check 'batch file restores .exe' ((Test-Path "$plain\run.exe") -and -not (Test-Path "$plain\run.zipper"))
    # Zipper: restores by itself and drops the batch file
    $auto = "$work\auto"; New-Item -ItemType Directory $auto | Out-Null; Copy-Item "$src\run.zip" $auto
    Run '/xhere', "`"$auto\run.zip`""
    Check 'Zipper restores .exe on extract' (((Get-Content "$auto\run.exe") -eq 'top-level executable fixture') -and -not (Test-Path "$auto\run.zipper"))
    Check 'Zipper removes the batch file' (-not (Test-Path "$auto\EXE 복원하기.bat"))
    $off = New-Item -ItemType Directory "$work\exe-off"; Copy-Item "$src\run.EXE" $off
    Set-ItemProperty $key RenameExe 0
    Run '/czip', "`"$off\run.EXE`""
    Check 'option off keeps .EXE' ((Names "$off\run.zip") -contains 'run.EXE')
    Set-ItemProperty $key RenameExe 1

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

    $folderNames = Names "$src\하위.7z"
    Check '7z keeps directory names and renames nested EXE to .zipper' (($folderNames -contains '하위\tools.exe\app.v2.zipper') -and ($folderNames -notcontains '하위\tools.exe\app.v2.ExE'))
    Check 'one folder: batch file goes inside it' (($folderNames -contains '하위\EXE 복원하기.bat') -and ($folderNames -notcontains 'EXE 복원하기.bat'))

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

    Check 'extracted executable bytes match the source' ((Get-FileHash "$here\하위\tools.exe\app.v2.exe").Hash -eq (Get-FileHash "$nestedDir\app.v2.ExE").Hash)

    # 4. split archives made elsewhere
    & $7z a -t7z -v1m "$work\split.7z" "$src\big.bin" -bso0 -bsp0
    & $7z a -tzip -v1m "$work\split.zip" "$src\big.bin" -bso0 -bsp0
    Run '/xfolder', "`"$work\split.7z.001`""
    Check 'split .7z.001' ((Get-FileHash "$work\split\big.bin").Hash -eq (Get-FileHash "$src\big.bin").Hash)
    Run '/xfolder', "`"$work\split.zip.001`""
    Check 'split .zip.001' ((Get-FileHash "$work\split\big_1.bin").Hash -eq (Get-FileHash "$src\big.bin").Hash)

    # 4b. tar.gz / tar.bz2 / tar.xz from a folder (made as tar, then compressed)
    Set-Content "$nestedDir\app.v2.zipper" 'name already taken'  # the renamed entry must get a free name
    foreach ($t in 'tar.gz', 'tar.bz2', 'tar.xz') {
        Run "/c:$t", "`"$src\하위`""
        $out = "$src\하위.$t"
        $unz = "$work\un-$t"; & $7z x $out "-o$unz" -bso0 -bsp0  # bz2/xz keep no inner name; 7-Zip derives 하위.tar
        Check "$t holds 하위.tar with the folder" ((Test-Path "$unz\하위.tar") -and ((Names "$unz\하위.tar") -contains '하위\deep.txt'))
    }
    # tools.exe holds app.v2.ExE, app.v2.zipper and the "app.v2 (2).exe" /xsmart restored above
    $tarNames = Names "$unz\하위.tar"
    $zipperNames = @($tarNames | Where-Object { $_ -like '하위\tools.exe\*.zipper' })
    Check '.zipper rename avoids taken names' (($zipperNames.Count -eq 3) -and (@($tarNames | Where-Object { $_ -like '*.exe' -and $_ -ne '하위\tools.exe' }).Count -eq 0))
    Run '/c:xz', "`"$src\가.txt`""
    & $7z x "$src\가.xz" "-o$work\un-xz" -bso0 -bsp0
    Check 'single-file xz' ((Get-Content "$work\un-xz\가") -eq '가나다')

    # 4c. one archive per item
    $ea = New-Item -ItemType Directory "$work\each"; 'a', 'b' | ForEach-Object { Set-Content "$ea\$_.txt" $_ }
    $ps = 'a', 'b' | ForEach-Object { Start-Process $exe -ArgumentList '/ceach:zip', "`"$ea\$_.txt`"" -PassThru }
    $ps | ForEach-Object { $_.WaitForExit(60000) | Out-Null }
    Check '/ceach makes one zip per item' ((Test-Path "$ea\a.zip") -and (Test-Path "$ea\b.zip") -and -not (Test-Path "$ea\each.zip"))

    # 4d. macOS names (decomposed Hangul) come out composed; the downloaded-file mark is kept
    $mac = New-Item -ItemType Directory "$work\mac-src"   # not "mac": that is where mac.zip extracts to
    $nfd = '폴더'.Normalize([Text.NormalizationForm]::FormD); $nfdFile = '한글.txt'.Normalize([Text.NormalizationForm]::FormD)
    New-Item -ItemType Directory "$mac\$nfd" | Out-Null; Set-Content "$mac\$nfd\$nfdFile" 'mac'
    & $7z a "$work\mac.zip" "$mac\$nfd" -bso0 -bsp0
    Set-Content -Path "$work\mac.zip" -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
    Run '/xfolder', "`"$work\mac.zip`""
    Check 'decomposed Hangul names are composed' ((Test-Path "$work\mac\폴더\한글.txt") -and -not (Test-Path "$work\mac\$nfd"))
    Check 'extracted file keeps the internet zone mark' ([bool](Get-Item "$work\mac\폴더\한글.txt" -Stream Zone.Identifier -ErrorAction SilentlyContinue))

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
    Set-Content -Path "$work\defaults.alz" -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
    Run '/xfolder', "`"$work\defaults.alz`""   # second run: files are renamed (_1), each gets the mark
    Check 'alz extraction keeps the internet zone mark' ([bool](Get-Item "$work\defaults\lorem_ipsum_short_1.txt" -Stream Zone.Identifier -ErrorAction SilentlyContinue))
}
finally {
    if ($null -eq $oldOpen) { Remove-ItemProperty $key OpenFolder } else { Set-ItemProperty $key OpenFolder $oldOpen }
    if ($null -eq $oldExe) { Remove-ItemProperty $key RenameExe } else { Set-ItemProperty $key RenameExe $oldExe }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { "$fail test(s) failed"; exit 1 } else { 'all passed' }
