# Drives the published app through a whole session on Windows and captures each screen:
# payload inspection, picking a device, flashing a full OTA, backup over adb, the log, About.
# With the stand-ins from tools/FastbootEnhance.FakeFastboot and FakeAdb it doubles as an
# end-to-end test: it fails unless every partition of the sample OTA reaches "fastboot flash",
# every critical partition of the stand-in phone is backed up with a matching SHA-256, and
# the chosen files are copied to the computer.
param(
    [Parameter(Mandatory)] [string] $App,
    [Parameter(Mandatory)] [string] $Payload,
    [Parameter(Mandatory)] [string] $Out,
    [int] $ExpectedFlashes = 0,
    [int] $ExpectedBackups = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
"@
[Win32]::SetProcessDPIAware() | Out-Null

$UIA = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
$Type = [System.Windows.Automation.ControlType]

New-Item -ItemType Directory -Force -Path $Out | Out-Null
# Screen names change as the UI does; start from an empty folder so nothing stale is kept.
Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force
$App = (Resolve-Path $App).Path
$Payload = (Resolve-Path $Payload).Path
$appDir = Split-Path $App
$crashLog = Join-Path $env:TEMP 'FastbootStudio\crash.log'
$fakeLog = Join-Path $appDir 'fake-fastboot.log'
$fakeAdbLog = Join-Path $appDir 'fake-adb.log'
$saveRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Fastboot Studio'
Remove-Item $crashLog, $fakeLog, $fakeAdbLog -ErrorAction SilentlyContinue
Remove-Item $saveRoot -Recurse -Force -ErrorAction SilentlyContinue

function Save-Desktop([string] $name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Fail([string] $why) {
    Save-Desktop "00-desktop-on-failure"
    if (Test-Path $crashLog) { Write-Host "---- crash.log ----"; Get-Content $crashLog | Write-Host }
    if (Test-Path $fakeLog) { Write-Host "---- fake-fastboot.log ----"; Get-Content $fakeLog | Write-Host }
    if (Test-Path $fakeAdbLog) { Write-Host "---- fake-adb.log ----"; Get-Content $fakeAdbLog | Write-Host }
    throw $why
}

# The window is captured from the screen, exactly as a user would see it. PrintWindow was
# used before, but WPF can hand it a frame where parts of the window are not yet repainted.
function Save-Window([string] $name) {
    [Win32]::SetForegroundWindow($script:hwnd) | Out-Null
    # RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_FRAME
    [Win32]::RedrawWindow($script:hwnd, [IntPtr]::Zero, [IntPtr]::Zero, 0x0585) | Out-Null
    Start-Sleep -Milliseconds 800
    # DWMWA_EXTENDED_FRAME_BOUNDS (9) leaves out the invisible resize border GetWindowRect counts.
    $r = New-Object Win32+RECT
    if ([Win32]::DwmGetWindowAttribute($script:hwnd, 9, [ref] $r, 16) -ne 0) {
        [Win32]::GetWindowRect($script:hwnd, [ref] $r) | Out-Null
    }
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $left = [Math]::Max($r.Left, $screen.Left); $top = [Math]::Max($r.Top, $screen.Top)
    $w = [Math]::Min($r.Right, $screen.Right) - $left
    $h = [Math]::Min($r.Bottom, $screen.Bottom) - $top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($left, $top, 0, 0, $bmp.Size)
    $path = Join-Path $Out "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $path (${w}x${h})"
}

function By-Id($parent, [string] $id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::AutomationIdProperty, $id)
    return $parent.FindFirst($Scope::Descendants, $cond)
}

function Wait-For([scriptblock] $probe, [int] $seconds, [string] $what) {
    for ($i = 0; $i -lt $seconds * 2; $i++) {
        $value = & $probe
        if ($null -ne $value -and $value -ne $false) { return $value }
        if ($script:proc.HasExited) { Fail "app exited while waiting for $what (code $($script:proc.ExitCode))" }
        Start-Sleep -Milliseconds 500
    }
    Fail "timed out waiting for $what"
}

# Grid and Border have no automation peer, so readiness is judged from a control that does.
function Shown($parent, [string] $id) {
    $e = By-Id $parent $id
    if ($null -ne $e -and -not $e.Current.IsOffscreen) { return $e }
    return $null
}

function Select-Item($element) {
    $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}

function Visible-Tabs($parent) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::TabItem)
    return @($parent.FindAll($Scope::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })
}

# A top-level window of the app other than the main one: a file dialog or a message box.
function Find-Dialog {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $script:proc.Id)
    foreach ($w in $UIA::RootElement.FindAll($Scope::Children, $cond)) {
        # Tool tips and popups are top-level windows of the process too; only real windows count.
        if ([IntPtr]$w.Current.NativeWindowHandle -ne $script:hwnd -and $w.Current.ControlType -eq $Type::Window) { return $w }
    }
    foreach ($w in $script:root.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Window)))) {
        return $w
    }
    return $null
}

# Presses the dialog's own button. Keys sent to the main window do not reach a modal box.
function Close-Dialog($dialog) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Button)
    $button = @($dialog.FindAll($Scope::Descendants, $cond)) | Select-Object -First 1
    if ($null -ne $button) {
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } else {
        [Win32]::SetForegroundWindow([IntPtr]$dialog.Current.NativeWindowHandle) | Out-Null
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
    Wait-For { $null -eq (Find-Dialog) } 10 "the dialog to close" | Out-Null
    Start-Sleep -Milliseconds 800
}

function Send([string] $keys) {
    [Win32]::SetForegroundWindow($script:hwnd) | Out-Null
    [System.Windows.Forms.SendKeys]::SendWait($keys)
}

# ---------------------------------------------------------------- launch
Write-Host "launching $App `"$Payload`""
$script:proc = Start-Process -FilePath $App -ArgumentList "`"$Payload`"" -WorkingDirectory $appDir -PassThru

$byProcess = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $proc.Id)
$script:root = Wait-For { $UIA::RootElement.FindFirst($Scope::Children, $byProcess) } 60 "the main window"
$script:hwnd = [IntPtr]$root.Current.NativeWindowHandle
Write-Host "window: '$($root.Current.Name)' hwnd=$hwnd"
# Fit the window on the runner's screen (often 1024x768) so a screen capture holds all of it.
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$fitW = [Math]::Min(1220, $work.Width); $fitH = [Math]::Min(780, $work.Height)
# SWP_NOZORDER | SWP_NOACTIVATE
[Win32]::SetWindowPos($hwnd, [IntPtr]::Zero, $work.Left, $work.Top, $fitW, $fitH, 0x0014) | Out-Null
Write-Host "work area $($work.Width)x$($work.Height), window set to ${fitW}x${fitH}"
[Win32]::SetForegroundWindow($hwnd) | Out-Null

$mainTabs = Wait-For { By-Id $root 'main_tabs' } 20 "main_tabs"
function Go([string] $tabId) {
    $tab = Wait-For { By-Id $mainTabs $tabId } 10 $tabId
    Select-Item $tab
    return $tab
}

# ---------------------------------------------------------------- payload inspector
$payloadTab = Go 'payload_tab'
Wait-For { Shown $root 'payload_info' } 60 "the payload to open" | Out-Null
$i = 1
foreach ($sub in (Visible-Tabs $payloadTab)) {
    Select-Item $sub
    Save-Window ('{0:D2}-payload-{1}' -f $i, ($sub.Current.Name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLower()); $i++
}

# ---------------------------------------------------------------- device list and device
$deviceTab = Go 'device_tab'
$devices = Wait-For { By-Id $root 'fastboot_devices_list' } 10 "the device list"
$rowCond = New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::DataItem)),
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::ListItem)))
$row = Wait-For { $devices.FindFirst($Scope::Descendants, $rowCond) } 20 "a device to appear"
Save-Window ('{0:D2}-device-list' -f $i); $i++

Select-Item $row
$row.SetFocus()
Send '{ENTER}'
Wait-For { Shown $root 'fastboot_info_list' } 20 "the device page" | Out-Null
Start-Sleep -Seconds 3   # getvar all

foreach ($sub in (Visible-Tabs $deviceTab)) {
    Select-Item $sub
    Save-Window ('{0:D2}-device-{1}' -f $i, ($sub.Current.Name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLower()); $i++
}

# ---------------------------------------------------------------- a confirmation dialog
# Erase asks first. Capture the question, answer No, and make sure nothing was erased.
$parts = Wait-For { By-Id $root 'fastboot_partition_list' } 10 "the partition list"
$part = Wait-For { $parts.FindFirst($Scope::Descendants, $rowCond) } 10 "a partition row"
Select-Item $part
$erase = Wait-For { By-Id $root 'fastboot_erase' } 10 "the erase button"
$erase.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$ask = Wait-For { Find-Dialog } 20 "the erase confirmation"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-confirm-erase' -f $i); $i++
Close-Dialog $ask
if (@(Get-Content $fakeLog | Where-Object { $_ -match ' erase ' }).Count -gt 0) { Fail "answering No still erased a partition" }

# ---------------------------------------------------------------- flash the sample OTA
if ($ExpectedFlashes -gt 0) {
    Go 'flash_tab' | Out-Null
    $button = Wait-For { By-Id $root 'fastboot_flash_payload' } 10 "the flash button"
    Wait-For { $button.Current.IsEnabled } 30 "the flash button to be enabled" | Out-Null
    $button.SetFocus()
    Send ' '

    $dialog = Wait-For { Find-Dialog } 20 "the file dialog"
    Write-Host "file dialog: '$($dialog.Current.Name)'"
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($Payload + '{ENTER}')

    # The file dialog takes a moment to go away; it must not be mistaken for the result.
    Wait-For { $null -eq (Find-Dialog) } 20 "the file dialog to close" | Out-Null

    # The app shows "operation completed" (or an error) when it is done. With the file dialog
    # gone, the next window to appear is that box. A message box does not expose its text to
    # UI Automation, so the fastboot log below is what proves the flash.
    $done = Wait-For { Find-Dialog } 240 "flashing to finish"
    Start-Sleep -Milliseconds 800
    $message = ($done.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
    Write-Host "dialog after flashing: $message"
    Save-Window ('{0:D2}-flash' -f $i); $i++
    Close-Dialog $done

    $flashes = @(Get-Content $fakeLog | Where-Object { $_ -match ' flash ' }).Count
    Write-Host "fastboot flash commands issued: $flashes (expected $ExpectedFlashes)"
    if ($flashes -ne $ExpectedFlashes) { Fail "expected $ExpectedFlashes flash commands, saw $flashes" }
}

# ---------------------------------------------------------------- backup over adb
function Press([string] $id) {
    $button = Wait-For { By-Id $root $id } 10 $id
    Wait-For { $button.Current.IsEnabled } 30 "$id to be enabled" | Out-Null
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Rows($list) {
    return @($list.FindAll($Scope::Descendants, $rowCond))
}

if ($ExpectedBackups -gt 0) {
    Go 'backup_tab' | Out-Null
    $adbList = Wait-For { By-Id $root 'backup_devices' } 10 "the adb device list"
    Wait-For { (Rows $adbList).Count -gt 0 } 30 "a device over adb" | Out-Null

    # Files: the list of /sdcard loads by itself once the device is picked.
    Select-Item (Wait-For { By-Id $root 'backup_files_tab' } 10 "the Files tab")
    $files = Wait-For { By-Id $root 'files_list' } 10 "the file list"
    Wait-For { (Rows $files).Count -ge 5 } 30 "the /sdcard listing" | Out-Null
    Press 'files_quick_documents'
    # The stand-in's Documents folder holds two files; /sdcard holds nine entries.
    Wait-For { (Rows $files).Count -eq 2 } 30 "the Documents listing" | Out-Null
    $docs = Rows $files
    Write-Host "documents listed: $($docs.Count)"
    $docs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    foreach ($row in ($docs | Select-Object -Skip 1)) {
        $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
    }
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-backup-files' -f $i); $i++
    Press 'files_pull'
    $pulled = Wait-For { Find-Dialog } 60 "the copy to finish"
    Start-Sleep -Milliseconds 600
    Close-Dialog $pulled

    $copied = @(Get-ChildItem (Join-Path $saveRoot 'Files') -Recurse -File -ErrorAction SilentlyContinue)
    Write-Host "files copied: $($copied.Name -join ', ')"
    if (-not ($copied.Name -contains "Ahmed's notes.txt") -or -not ($copied.Name -contains 'invoice 2026.pdf')) {
        Fail "the selected documents were not copied to $saveRoot\Files"
    }

    # Partitions: read the table (critical ones come pre-selected), back them up, check hashes.
    Select-Item (Wait-For { By-Id $root 'backup_partitions_tab' } 10 "the Partitions tab")
    Press 'backup_read'
    $parts = Wait-For { By-Id $root 'backup_partition_list' } 10 "the partition list"
    Wait-For { (Rows $parts).Count -ge 10 } 60 "the partition table" | Out-Null
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-backup-partitions' -f $i); $i++

    Press 'backup_start'
    $done = Wait-For { Find-Dialog } 240 "the backup to finish"
    Start-Sleep -Milliseconds 800
    Save-Window ('{0:D2}-backup-done' -f $i); $i++
    Close-Dialog $done

    $folder = Get-ChildItem (Join-Path $saveRoot 'Backups') -Directory | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($null -eq $folder) { Fail "no backup folder was written under $saveRoot\Backups" }
    $sums = @(Get-Content (Join-Path $folder.FullName 'SHA256SUMS') | Where-Object { $_ })
    Write-Host "backup in $($folder.Name): $($sums.Count) images"
    if ($sums.Count -ne $ExpectedBackups) { Fail "expected $ExpectedBackups images in SHA256SUMS, found $($sums.Count)" }
    foreach ($line in $sums) {
        $hash, $name = $line -split '  ', 2
        $actual = (Get-FileHash (Join-Path $folder.FullName $name) -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $hash) { Fail "$name does not match its recorded SHA-256" }
    }
    if (-not (Test-Path (Join-Path $folder.FullName 'backup-info.txt'))) { Fail "backup-info.txt is missing" }
    $reads = @(Get-Content $fakeAdbLog | Where-Object { $_ -match 'exec-out' }).Count
    Write-Host "partitions read over adb: $reads; all hashes match"
}

# ---------------------------------------------------------------- log and about
Go 'logs_tab' | Out-Null
Save-Window ('{0:D2}-logs' -f $i); $i++
Go 'about_tab' | Out-Null
Save-Window ('{0:D2}-about' -f $i); $i++

if (Test-Path $crashLog) { Fail "the app recorded an unhandled exception" }
if ($proc.HasExited) { Fail "app exited while being captured, code $($proc.ExitCode)" }
Stop-Process -Id $proc.Id -Force
Write-Host "captured $($i - 1) screens"
