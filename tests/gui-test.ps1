# Drives the dialogs with UI Automation: split + password compress, password prompt, main window.
# Screenshots go to tests\screens. Run after building Release:
#   powershell -ExecutionPolicy Bypass -File tests\gui-test.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$exe = Join-Path $root 'src\Zipper\bin\Release\net48\Zipper.exe'
$7z = Join-Path (Split-Path $exe) '7z.exe'
$shots = Join-Path $root 'tests\screens'; New-Item -ItemType Directory $shots -Force | Out-Null
$work = Join-Path $env:TEMP ('zipper-gui-' + [guid]::NewGuid().ToString('N').Substring(0, 8)); New-Item -ItemType Directory "$work\docs" | Out-Null
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$fail = 0
function Check($name, $ok) { if ($ok) { "PASS  $name" } else { "FAIL  $name"; $script:fail++ } }

function Wait-Window($title, $parent = $A::RootElement, $timeout = 15) {
    $cond = [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, $title)
    for ($i = 0; $i -lt $timeout * 5; $i++) {
        $w = $parent.FindFirst($T::Children, $cond)  # owned dialogs are children of their owner
        if ($w) { Start-Sleep -Milliseconds 400; return $w }
        Start-Sleep -Milliseconds 200
    }
    throw "window not found: $title"
}
function Find($win, $name) {
    $e = $win.FindFirst($T::Descendants, [System.Windows.Automation.PropertyCondition]::new($A::NameProperty, $name))
    if (-not $e) { throw "control not found: $name" }; $e
}
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); [DllImport("user32.dll")] public static extern void mouse_event(int f, int x, int y, int d, int e);'
# WinForms controls show up as plain panes to managed UIA, so drive them with real input
function Front($win) { [W.U]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null; Start-Sleep -Milliseconds 300 }
function Click($e) { $r = $e.Current.BoundingRectangle; [W.U]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); [W.U]::mouse_event(2, 0, 0, 0, 0); [W.U]::mouse_event(4, 0, 0, 0, 0); Start-Sleep -Milliseconds 300 }
function Keys($text) { [System.Windows.Forms.SendKeys]::SendWait($text); Start-Sleep -Milliseconds 200 }
# paste text instead of typing it, so the Korean IME state cannot change what arrives
function Paste-Text($text) { [System.Windows.Forms.Clipboard]::SetText($text); Keys '^v' }
function Shot($win, $name) {
    $r = $win.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save((Join-Path $shots "$name.png")); $bmp.Dispose()
}

$key = 'HKCU:\Software\Zipper'; New-Item $key -Force | Out-Null
$oldOpen = (Get-ItemProperty $key -ErrorAction SilentlyContinue).OpenFolder
Set-ItemProperty $key OpenFolder 0
try {
    $big = New-Object byte[] (2500KB); (New-Object Random 7).NextBytes($big); [IO.File]::WriteAllBytes("$work\docs\data.bin", $big)
    Set-Content "$work\docs\메모.txt" '안녕하세요' -Encoding UTF8

    # 1. compress dialog: zip, split 1 MB, password
    $p = Start-Process $exe -ArgumentList '/c', "`"$work\docs`"" -PassThru
    $w = Wait-Window '새로 압축'; Front $w
    Click (Find $w '분할 안 함'); Keys '^a'; Paste-Text '1 MB'
    Keys '{TAB}'; Paste-Text 'p@ss "word"\'
    Shot $w 'compress'
    Keys '{ENTER}'
    $p.WaitForExit(30000) | Out-Null
    $vols = Get-ChildItem "$work\docs.zip.*"
    Check 'split into 3 volumes' ($vols.Count -eq 3)
    # PowerShell 5.1 pipes mangle stdin, so feed 7z the password from a file
    [IO.File]::WriteAllText("$work\pw.txt", "p@ss `"word`"\`r`n")
    $test = Start-Process $7z -ArgumentList 't', "`"$work\docs.zip.001`"", '-bso0', '-bsp0' -RedirectStandardInput "$work\pw.txt" -NoNewWindow -Wait -PassThru
    Check 'password with quotes/backslash works' ($test.ExitCode -eq 0)

    # 2. open the split, encrypted archive: password prompt (wrong first), then the browser
    $p = Start-Process $exe -ArgumentList "`"$work\docs.zip.001`"" -PassThru
    $w = Wait-Window '암호 입력'; Front $w
    Paste-Text 'wrong'; Keys '{ENTER}'
    Start-Sleep 2
    $w = Wait-Window '암호 입력'; Front $w
    Shot $w 'password-wrong'
    Paste-Text 'p@ss "word"\'; Keys '{ENTER}'
    $main = Wait-Window 'docs.zip.001 - Zipper'; Front $main
    Shot $main 'main-root'
    Keys '{DOWN}{ENTER}'   # into the "docs" folder
    Start-Sleep 1
    Shot $main 'main-folder'

    Keys '{BACKSPACE}^e'
    $x = Wait-Window '압축 풀기' $main; Front $x
    Shot $x 'extract'
    Keys '^a'; Paste-Text "$work\out"; Keys '{ENTER}'
    for ($i = 0; $i -lt 50 -and -not (Test-Path "$work\out\docs\data.bin"); $i++) { Start-Sleep -Milliseconds 200 }
    Start-Sleep 1
    Check 'extract from browser with remembered password' ((Get-FileHash "$work\out\docs\data.bin").Hash -eq (Get-FileHash "$work\docs\data.bin").Hash)

    Front $main; Click (Find $main '설정')
    $s = Wait-Window 'Zipper 설정' $main
    Shot $s 'settings'
    Front $s; Keys '{ESC}'
    $p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null

    # 3. empty main window
    $p = Start-Process $exe -PassThru
    $main = Wait-Window 'Zipper'
    Shot $main 'main-empty'
    $p.CloseMainWindow() | Out-Null; $p.WaitForExit(5000) | Out-Null

    # 4. AES-256 encrypted EGG: wrong password must not leave files, right one extracts
    Copy-Item "$root\tests\data\lorem_long_aes256.egg" $work
    $p = Start-Process $exe -ArgumentList '/xfolder', "`"$work\lorem_long_aes256.egg`"" -PassThru
    $w = Wait-Window '암호 입력'; Front $w
    Paste-Text 'wrong'; Keys '{ENTER}'
    Start-Sleep 2
    Check 'egg: wrong password leaves nothing' (-not (Test-Path "$work\lorem_long_aes256"))
    $w = Wait-Window '암호 입력'; Front $w
    Paste-Text 'password12345!'; Keys '{ENTER}'
    $p.WaitForExit(20000) | Out-Null
    $txt = Get-ChildItem "$work\lorem_long_aes256" -Recurse -File -ErrorAction SilentlyContinue
    Check 'egg: AES-256 extracts with password' ($txt.Count -ge 1 -and ($txt | Where-Object Name -eq 'lorem_ipsum_long.txt').Length -gt 15000)

    # 4b. encrypted ZIP with many files: the parallel extraction gets the password in every 7z.exe
    $ez = New-Item -ItemType Directory "$work\ezip\src"; 1..30 | ForEach-Object { Set-Content "$ez\e$_.txt" "secret $_" }
    [IO.File]::WriteAllText("$work\ezip\pw.txt", "zip-pass`r`nzip-pass`r`n")
    Start-Process $7z -ArgumentList 'a', '-tzip', '-p', "`"$work\ezip\e.zip`"", "`"$ez\*`"", '-bso0', '-bsp0' -RedirectStandardInput "$work\ezip\pw.txt" -NoNewWindow -Wait
    $p = Start-Process $exe -ArgumentList '/xfolder', "`"$work\ezip\e.zip`"" -PassThru
    $w = Wait-Window '암호 입력'; Front $w
    Paste-Text 'zip-pass'; Keys '{ENTER}'
    $p.WaitForExit(30000) | Out-Null
    $out = Get-ChildItem "$work\ezip\e" -File -ErrorAction SilentlyContinue
    Check 'encrypted zip: all files extracted in parallel' ($out.Count -eq 30 -and (Get-Content "$work\ezip\e\e7.txt") -eq 'secret 7')

    # 5. Explorer selection: Zipper reads it instead of waiting for the other processes
    $sel = New-Item -ItemType Directory "$work\sel"; 'a' | Set-Content "$sel\s1.txt"; 'b' | Set-Content "$sel\s2.txt"
    $shell = New-Object -ComObject Shell.Application
    $shell.Open($sel.FullName); Start-Sleep 3
    $ew = @($shell.Windows()) | Where-Object { try { $_.Document.Folder.Self.Path -eq $sel.FullName } catch { $false } } | Select-Object -First 1
    try {
        $ew.Document.SelectItem($ew.Document.Folder.ParseName('s1.txt'), 1 + 4 + 8 + 16)
        $ew.Document.SelectItem($ew.Document.Folder.ParseName('s2.txt'), 1); Start-Sleep 1
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $ps = 's1.txt', 's2.txt' | ForEach-Object { Start-Process $exe -ArgumentList '/czip', "`"$sel\$_`"" -PassThru }
        $ps | ForEach-Object { $_.WaitForExit(20000) | Out-Null }
        '      two selected files compressed in {0:N2}s' -f $sw.Elapsed.TotalSeconds
        Check 'explorer selection: two processes make one archive' ((Test-Path "$sel\sel.zip") -and -not (Test-Path "$sel\s1.zip") -and -not (Test-Path "$sel\s2.zip"))
    }
    finally { $ew.Quit() }
}
finally {
    Get-Process Zipper -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($null -eq $oldOpen) { Remove-ItemProperty $key OpenFolder } else { Set-ItemProperty $key OpenFolder $oldOpen }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($fail) { "$fail test(s) failed"; exit 1 } else { 'all passed' }
