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
    [int] $ExpectedBackups = 0,
    [switch] $Arabic
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
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    public static IntPtr CurrentCursor() {
        CURSORINFO ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(ci);
        return GetCursorInfo(ref ci) ? ci.hCursor : IntPtr.Zero;
    }
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
# The stand-ins read these to play a phone in another mode; see FakeAdb and FakeFastboot.
$adbMode = Join-Path $appDir 'fake-adb.mode'
$fastbootMode = Join-Path $appDir 'fake-fastboot.mode'
$saveRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Fastboot Studio'
Remove-Item $crashLog, $fakeLog, $fakeAdbLog, $adbMode, $fastbootMode -ErrorAction SilentlyContinue

# What the USB check reads instead of Windows' device list: a phone without a driver and one
# in MediaTek preloader mode (see UsbScan.cs).
$fakeUsb = Join-Path $appDir 'fake-usb.txt'
Set-Content $fakeUsb @(
    'Android|USB\VID_0E8D&PID_201C\0123456789ABCDEF|28|',
    'MediaTek PreLoader USB VCOM (Android) (COM5)|USB\VID_0E8D&PID_2000\5&2B1E&0&1|0|Ports'
)
$env:FASTBOOT_STUDIO_FAKE_USB = $fakeUsb
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
function Launch-App {
    Write-Host "launching $App `"$Payload`""
    $script:proc = Start-Process -FilePath $App -ArgumentList "`"$Payload`"" -WorkingDirectory $appDir -PassThru

    $byProcess = New-Object System.Windows.Automation.PropertyCondition($UIA::ProcessIdProperty, $script:proc.Id)
    $script:root = Wait-For { $UIA::RootElement.FindFirst($Scope::Children, $byProcess) } 60 "the main window"
    $script:hwnd = [IntPtr]$script:root.Current.NativeWindowHandle
    Write-Host "window: '$($script:root.Current.Name)' hwnd=$($script:hwnd)"
    # Fit the window on the runner's screen (often 1024x768) so a screen capture holds all of it.
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $fitW = [Math]::Min(1220, $work.Width); $fitH = [Math]::Min(780, $work.Height)
    # SWP_NOZORDER | SWP_NOACTIVATE
    [Win32]::SetWindowPos($script:hwnd, [IntPtr]::Zero, $work.Left, $work.Top, $fitW, $fitH, 0x0014) | Out-Null
    Write-Host "work area $($work.Width)x$($work.Height), window set to ${fitW}x${fitH}"
    [Win32]::SetForegroundWindow($script:hwnd) | Out-Null
    $script:mainTabs = Wait-For { By-Id $script:root 'main_tabs' } 20 "main_tabs"
}

Launch-App
function Go([string] $tabId) {
    $tab = Wait-For { By-Id $mainTabs $tabId } 10 $tabId
    Select-Item $tab
    return $tab
}

# ---------------------------------------------------------------- helpers
function Press([string] $id) {
    $button = Wait-For { By-Id $root $id } 10 $id
    Wait-For { $button.Current.IsEnabled } 30 "$id to be enabled" | Out-Null
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Rows($list) {
    return @($list.FindAll($Scope::Descendants, $rowCond))
}

# Picks a file in the open file dialog, then waits for that dialog (not whatever window
# comes next) to go away.
function Choose-File([string] $path, [string] $what) {
    $dialog = Wait-For { Find-Dialog } 20 $what
    $fileHwnd = $dialog.Current.NativeWindowHandle
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($path + '{ENTER}')
    Wait-For { $d = Find-Dialog; $null -eq $d -or $d.Current.NativeWindowHandle -ne $fileHwnd } 20 "$what to close" | Out-Null
}

# Presses a dialog button by its label, for questions where the first button is not the answer.
function Answer-Dialog($dialog, [string] $label) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Button)
    $button = @($dialog.FindAll($Scope::Descendants, $cond)) | Where-Object { $_.Current.Name -eq $label } | Select-Object -First 1
    if ($null -eq $button) { Fail "the dialog has no '$label' button" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-For { $null -eq (Find-Dialog) } 10 "the dialog to close" | Out-Null
    Start-Sleep -Milliseconds 800
}

function Fastboot-Log([string] $pattern) {
    return @(Get-Content $fakeLog | Where-Object { $_ -match $pattern }).Count
}

# ---------------------------------------------------------------- payload inspector
$payloadTab = Go 'payload_tab'
Wait-For { Shown $root 'payload_info' } 60 "the payload to open" | Out-Null
$i = 1
foreach ($sub in (Visible-Tabs $payloadTab)) {
    Select-Item $sub
    Save-Window ('{0:D2}-payload-{1}' -f $i, ($sub.Current.Name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLower()); $i++
}

# ---------------------------------------------------------------- the mouse cursor
# The hand belongs on what can be clicked: a navigation entry shows it, the page it opens
# does not. (A cursor set on a tab used to reach the whole page under it.)
function Cursor-Over($element) {
    $r = $element.Current.BoundingRectangle
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    [Win32]::SetForegroundWindow($script:hwnd) | Out-Null
    [Win32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 400
    [Win32]::SetCursorPos($x + 2, $y + 1) | Out-Null   # a move makes WPF ask for the cursor again
    Start-Sleep -Milliseconds 400
    return [Win32]::CurrentCursor()
}
$arrow = [Win32]::LoadCursor([IntPtr]::Zero, [IntPtr]32512)   # IDC_ARROW
$hand = [Win32]::LoadCursor([IntPtr]::Zero, [IntPtr]32649)    # IDC_HAND
Select-Item (Visible-Tabs $payloadTab)[0]   # the loop above left the last sub-tab open
$onPage = Cursor-Over (Wait-For { Shown $root 'payload_info' } 10 "the payload properties")
$onNav = Cursor-Over (Wait-For { By-Id $mainTabs 'device_tab' } 10 "the Device entry")
Write-Host "cursor over the page: $onPage (arrow $arrow), over a navigation entry: $onNav (hand $hand)"
if ($onPage -eq [IntPtr]::Zero -or $onNav -eq [IntPtr]::Zero) {
    Write-Host "the runner reports no cursor; skipping the cursor check"
} else {
    if ($onPage -ne $arrow) { Fail "the payload page shows a cursor other than the arrow" }
    if ($onNav -ne $hand) { Fail "a navigation entry does not show the hand cursor" }
}

# ---------------------------------------------------------------- device list and device
$deviceTab = Go 'device_tab'
$devices = Wait-For { By-Id $root 'fastboot_devices_list' } 10 "the device list"
$rowCond = New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::DataItem)),
    (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::ListItem)))
$row = Wait-For { $devices.FindFirst($Scope::Descendants, $rowCond) } 20 "a device to appear"
Save-Window ('{0:D2}-device-list' -f $i); $i++

# The USB check explains what Windows sees: here a phone without a driver and one in
# MediaTek preloader mode. It offers Device Manager; answer No.
Press 'fastboot_usb_check'
$usb = Wait-For { Find-Dialog } 20 "the USB check"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-usb-check' -f $i); $i++
$usbText = ($usb.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
    ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
Answer-Dialog $usb 'No'
Write-Host "usb check: $usbText"

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
    $fileHwnd = $dialog.Current.NativeWindowHandle
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($Payload + '{ENTER}')

    # The file dialog takes a moment to go away; it must not be mistaken for the result.
    Wait-For { $d = Find-Dialog; $null -eq $d -or $d.Current.NativeWindowHandle -ne $fileHwnd } 20 "the file dialog to close" | Out-Null

    # The stand-in phone has no odm: the app offers to create it in super first.
    $create = Wait-For { Find-Dialog } 30 "the missing partition question"
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-create-missing' -f $i); $i++
    Answer-Dialog $create 'Yes'

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
    # odm_a is created (empty) before anything is flashed, and odm is then written to it.
    $lines = @(Get-Content $fakeLog)
    $created = -1; $firstFlash = -1
    for ($n = 0; $n -lt $lines.Count; $n++) {
        if ($created -lt 0 -and $lines[$n] -match 'create-logical-partition odm_a 0') { $created = $n }
        if ($firstFlash -lt 0 -and $lines[$n] -match ' flash ') { $firstFlash = $n }
    }
    if ($created -lt 0) { Fail "odm_a was not created before flashing the OTA" }
    if ($created -gt $firstFlash) { Fail "odm_a was created after flashing had started" }
    if ((Fastboot-Log ' flash odm ') -ne 1) { Fail "odm was not flashed after it was created" }
    Write-Host "missing partition: odm_a created, then written"
}

# ---------------------------------------------------------------- backup over adb
# Hashes every image a backup recorded in SHA256SUMS against the file it wrote.
function Check-Backup([string] $what) {
    $folder = Get-ChildItem (Join-Path $saveRoot 'Backups') -Directory | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($null -eq $folder) { Fail "no backup folder was written under $saveRoot\Backups ($what)" }
    $sums = @(Get-Content (Join-Path $folder.FullName 'SHA256SUMS') | Where-Object { $_ })
    Write-Host "backup in $($folder.Name) ($what): $($sums.Count) images"
    if ($sums.Count -ne $ExpectedBackups) { Fail "expected $ExpectedBackups images in SHA256SUMS, found $($sums.Count) ($what)" }
    foreach ($line in $sums) {
        $hash, $name = $line -split '  ', 2
        $actual = (Get-FileHash (Join-Path $folder.FullName $name) -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $hash) { Fail "$name does not match its recorded SHA-256 ($what)" }
    }
    if (-not (Test-Path (Join-Path $folder.FullName 'backup-info.txt'))) { Fail "backup-info.txt is missing ($what)" }
}

$twrp = Join-Path (Split-Path $Payload) 'twrp-sample.img'

# ---------------------------------------------------------------- device safety
# Switching to a slot whose logical partitions are empty is refused; a boot-chain partition
# needs its name typed; vbmeta can be flashed with verification off; "boot once" refuses an
# init_boot image and goes through the bootloader, since fastbootd cannot boot an image.
$initBoot = Join-Path (Split-Path $Payload) 'init_boot-sample.img'
Remove-Item $fastbootMode -ErrorAction SilentlyContinue
$deviceTab = Go 'device_tab'
Start-Sleep -Seconds 1
# The device stays open after flashing, on whichever of its tabs was last shown.
if ($null -eq (Shown $root 'device_basic_tab')) {
    $row = Wait-For { $list = Shown $root 'fastboot_devices_list'; if ($null -ne $list) { $list.FindFirst($Scope::Descendants, $rowCond) } } 20 "a device to appear"
    Select-Item $row
    $row.SetFocus()
    Send '{ENTER}'
    Start-Sleep -Seconds 3   # getvar all
}
Select-Item (Wait-For { Shown $root 'device_basic_tab' } 20 "the device page")
Wait-For { Shown $root 'fastboot_info_list' } 20 "the basic properties" | Out-Null

function Filter-Partitions([string] $name) {
    Select-Item (Wait-For { By-Id $root 'device_partitions_tab' } 10 "the partition table tab")
    $box = Wait-For { By-Id $root 'fastboot_partition_name_textbox' } 10 "the partition filter"
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($name)
    Start-Sleep -Milliseconds 600
    $list = Wait-For { By-Id $root 'fastboot_partition_list' } 10 "the partition list"
    # The filter matches by substring; each name used here matches one partition only.
    Wait-For { (Rows $list).Count -eq 1 } 10 "the list to show only $name" | Out-Null
    Select-Item (Rows $list)[0]
}

function Dialog-Button($dialog, [string] $label) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Button)
    return @($dialog.FindAll($Scope::Descendants, $cond)) | Where-Object { $_.Current.Name -eq $label } | Select-Object -First 1
}

# The stand-in sits in fastbootd with system_b, vendor_b... at size 0, as after a factory super.
# Counts start here: flashing the OTA above may have sent some of these commands already.
$activesBefore = Fastboot-Log 'set_active'
$bootsBefore = Fastboot-Log '-s \S+ boot '
$rebootsBefore = Fastboot-Log 'reboot bootloader'
Select-Item (Wait-For { By-Id $root 'device_basic_tab' } 10 "the basic tab")
Press 'fastboot_ab_switch'
$blocked = Wait-For { Find-Dialog } 20 "the slot switch refusal"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-slot-switch-blocked' -f $i); $i++
if ($null -ne (Dialog-Button $blocked 'Yes')) { Fail "switching to a slot with empty partitions was offered, not refused" }
Close-Dialog $blocked
if ((Fastboot-Log 'set_active') -gt $activesBefore) { Fail "set_active was sent to a slot with empty partitions" }
Write-Host "slot switch: refused, b has empty logical partitions"

# abl is part of the boot chain: Yes stays disabled until the name is typed.
Filter-Partitions 'abl_a'
Press 'fastboot_erase'
$typedAsk = Wait-For { Find-Dialog } 20 "the critical partition confirmation"
Start-Sleep -Milliseconds 600
$yes = Dialog-Button $typedAsk 'Yes'
if ($null -eq $yes) { Fail "the critical partition confirmation has no Yes button" }
if ($yes.Current.IsEnabled) { Fail "Yes is enabled before the partition name is typed" }
$typedBox = Wait-For { $typedAsk.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Edit))) } 10 "the name box"
$typedBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('abl')
Start-Sleep -Milliseconds 400
if ($yes.Current.IsEnabled) { Fail "Yes is enabled with the wrong name typed" }
$typedBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('abl_a')
Wait-For { $yes.Current.IsEnabled } 5 "Yes to be enabled once the name is typed" | Out-Null
Save-Window ('{0:D2}-critical-confirm' -f $i); $i++
Answer-Dialog $typedAsk 'Yes'
Wait-For { (Fastboot-Log 'erase abl_a') -gt 0 } 20 "fastboot erase abl_a" | Out-Null
Write-Host "critical partition: erase sent only after the name was typed"

# vbmeta: the question about disabling verification, answered Yes, adds both flags.
Filter-Partitions 'vbmeta_a'
Press 'fastboot_flash'
Choose-File $twrp "the vbmeta image dialog"
$vbAsk = Wait-For { Find-Dialog } 20 "the vbmeta question"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-vbmeta-question' -f $i); $i++
Answer-Dialog $vbAsk 'Yes'
$vbDone = Wait-For { Find-Dialog } 30 "vbmeta to be flashed"
Close-Dialog $vbDone
if ((Fastboot-Log 'flash --disable-verity --disable-verification vbmeta_a ') -ne 1) { Fail "vbmeta was not flashed with --disable-verity --disable-verification" }
Write-Host "vbmeta: flashed with verification disabled"
# Super: the card on the partition tab, and a 12 GiB image (a sparse file of a few bytes)
# that cannot fit; the app says by how much and what could make room. Answer No.
Filter-Partitions 'product_a'
Save-Window ('{0:D2}-super-space' -f $i); $i++
Press 'fastboot_flash'
Choose-File (Join-Path (Split-Path $Payload) 'big-sparse.img') "the image dialog"
$full = Wait-For { Find-Dialog } 20 "the super space warning"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-super-full' -f $i); $i++
Answer-Dialog $full 'No'
if ((Fastboot-Log 'flash product_a .*big-sparse') -gt 0) { Fail "an image too big for super was flashed" }
Write-Host "super: a 12 GiB image was stopped before flashing"
(By-Id $root 'fastboot_partition_name_textbox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')

# Boot once: init_boot has no kernel and is refused before anything is sent.
Select-Item (Wait-For { By-Id $root 'device_basic_tab' } 10 "the basic tab")
Press 'fastboot_boot_once'
Choose-File $initBoot "the boot image dialog"
$refused = Wait-For { Find-Dialog } 20 "the init_boot refusal"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-boot-once-init-boot' -f $i); $i++
Close-Dialog $refused
if ((Fastboot-Log '-s \S+ boot ') -gt $bootsBefore -or (Fastboot-Log 'reboot bootloader') -gt $rebootsBefore) { Fail "an init_boot image was sent to boot" }

# In fastbootd it offers the bootloader first, then boots the image from there.
$getvars = Fastboot-Log 'getvar all'
Press 'fastboot_boot_once'
Choose-File $twrp "the boot image dialog"
$toBootloader = Wait-For { Find-Dialog } 20 "the fastbootd question"
Answer-Dialog $toBootloader 'Yes'
Wait-For { (Fastboot-Log 'reboot bootloader') -gt $rebootsBefore } 20 "fastboot reboot bootloader" | Out-Null
Wait-For { (Fastboot-Log 'getvar all') -gt $getvars } 30 "the device to be read again" | Out-Null
Start-Sleep -Seconds 2
if ((Fastboot-Log '-s \S+ boot ') -gt $bootsBefore) { Fail "the image was sent to fastbootd, which cannot boot it" }

Press 'fastboot_boot_once'
Choose-File $twrp "the boot image dialog"
$started = Wait-For { Find-Dialog } 30 "the image to boot"
Start-Sleep -Milliseconds 600
Save-Window ('{0:D2}-boot-once-started' -f $i); $i++
Close-Dialog $started
if ((Fastboot-Log '-s \S+ boot ') -ne $bootsBefore + 1) { Fail "fastboot boot of the image was not issued once" }
Wait-For { Shown $root 'fastboot_devices_list' } 20 "the device list after booting" | Out-Null
Write-Host "boot once: init_boot refused, bootloader first, then booted"

# Back to fastbootd for the backup below.
Remove-Item $fastbootMode -ErrorAction SilentlyContinue

if ($ExpectedBackups -gt 0) {
    # ------------------------------------------------------------ phone in fastboot
    # Nothing on adb, the phone in fastboot: the page explains and offers a way out instead
    # of waiting for a device forever.
    # The device steps above booted an image too; count only what the backup page sends.
    $rebootsBefore = Fastboot-Log 'reboot bootloader'
    $bootsBefore = Fastboot-Log '-s \S+ boot '
    Set-Content $adbMode 'none'
    Go 'backup_tab' | Out-Null
    Wait-For { Shown $root 'backup_fb_reboot_recovery' } 30 "the fastboot panel" | Out-Null
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-backup-fastboot' -f $i); $i++

    Press 'backup_fb_reboot_recovery'
    Wait-For { (Fastboot-Log 'reboot recovery') -gt 0 } 20 "fastboot reboot recovery" | Out-Null
    Write-Host "fastboot panel: reboot recovery sent"

    # The stand-in sits in fastbootd, which cannot boot an image: the app offers to go to
    # the bootloader first. Then the image is sent for real.
    Press 'backup_fb_boot_image'
    Choose-File $twrp "the recovery image dialog"
    $ask = Wait-For { Find-Dialog } 30 "the fastbootd question"
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-backup-fastbootd-question' -f $i); $i++
    Answer-Dialog $ask 'Yes'
    Wait-For { (Fastboot-Log 'reboot bootloader') -gt $rebootsBefore } 20 "fastboot reboot bootloader" | Out-Null
    if ((Fastboot-Log '-s \S+ boot ') -gt $bootsBefore) { Fail "the image was sent to fastbootd, which cannot boot it" }

    Press 'backup_fb_boot_image'
    Choose-File $twrp "the recovery image dialog"
    $booted = Wait-For { Find-Dialog } 30 "the recovery image to boot"
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-backup-recovery-booted' -f $i); $i++
    Close-Dialog $booted
    if ((Fastboot-Log '-s \S+ boot .*twrp-sample\.img') -ne $bootsBefore + 1) { Fail "fastboot boot of the recovery image was not issued once" }
    Write-Host "fastboot panel: recovery image booted"

    # The "recovery" comes up on adb: the panel gives way to the backup tabs.
    Remove-Item $adbMode, $fastbootMode -ErrorAction SilentlyContinue
    Wait-For { $null -eq (Shown $root 'backup_fb_reboot_recovery') } 30 "the fastboot panel to go" | Out-Null

    # ------------------------------------------------------------ custom recovery
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
    # Select both files the way a user would: click one, then Ctrl+A.
    $docs[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $docs[0].SetFocus()
    Send '^a'
    Start-Sleep -Milliseconds 600
    $picked = @($files.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()).Count
    Write-Host "documents selected: $picked"
    if ($picked -ne $docs.Count) { Fail "Ctrl+A selected $picked of $($docs.Count) files" }
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
    # Only rows on screen are created (the list is virtualised), so a few rows is enough to
    # know the table arrived; SHA256SUMS below checks that every critical partition was saved.
    Wait-For { (Rows $parts).Count -ge 3 } 60 "the partition table" | Out-Null
    Write-Host "partition rows on screen: $((Rows $parts).Count)"
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-backup-partitions' -f $i); $i++

    Press 'backup_start'
    $done = Wait-For { Find-Dialog } 240 "the backup to finish"
    Start-Sleep -Milliseconds 800
    Save-Window ('{0:D2}-backup-done' -f $i); $i++
    Close-Dialog $done

    Check-Backup 'recovery'
    $reads = @(Get-Content $fakeAdbLog | Where-Object { $_ -match 'exec-out' }).Count
    Write-Host "partitions read over adb: $reads; all hashes match"
}

# ---------------------------------------------------------------- image tools
# Opens the sample sparse super (three parts) through the file dialog, extracts its
# partitions and expands it to raw, then checks every SHA-256 against what SampleGen wrote.
$sampleDir = Split-Path $Payload
$superPart = Join-Path $sampleDir 'super.img_sparsechunk.0'
$expectedFile = Join-Path $sampleDir 'super-expected.sha256'

function Open-Image([string] $path) {
    Press 'images_open'
    $dialog = Wait-For { Find-Dialog } 20 "the image file dialog"
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait($path + '{ENTER}')
    Wait-For { $null -eq (Find-Dialog) } 20 "the image file dialog to close" | Out-Null
}

if (Test-Path $superPart) {
    Go 'images_tab' | Out-Null
    Save-Window ('{0:D2}-images-empty' -f $i); $i++
    Open-Image $superPart
    $superList = Wait-For { By-Id $root 'images_partitions' } 30 "the super partition list"
    Wait-For { (Rows $superList).Count -ge 3 } 60 "the super partitions" | Out-Null
    Start-Sleep -Milliseconds 600
    Save-Window ('{0:D2}-images-super' -f $i); $i++

    Press 'images_extract'
    $done = Wait-For { Find-Dialog } 120 "the extraction to finish"
    Start-Sleep -Milliseconds 800
    Save-Window ('{0:D2}-images-extracted' -f $i); $i++
    Close-Dialog $done

    $expected = @{}
    foreach ($line in Get-Content $expectedFile) { $h, $n = $line -split '  ', 2; $expected[$n] = $h }
    $unpacked = Join-Path $sampleDir 'super_unpacked'
    foreach ($name in @('system_a.img', 'system_ext_a.img', 'product_a.img', 'vendor_a.img', 'odm_a.img')) {
        $file = Join-Path $unpacked $name
        if (-not (Test-Path $file)) { Fail "$name was not extracted" }
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $expected[$name]) { Fail "$name does not match its expected SHA-256" }
    }
    Write-Host "super: 5 partitions extracted from 3 sparse parts, all SHA-256 match"

    Select-Item (Wait-For { By-Id $root 'images_details_tab' } 10 "the Details tab")
    Start-Sleep -Milliseconds 500
    Save-Window ('{0:D2}-images-details' -f $i); $i++

    Press 'images_to_raw'
    $done = Wait-For { Find-Dialog } 120 "the conversion to raw"
    Start-Sleep -Milliseconds 600
    Close-Dialog $done
    $rawHash = (Get-FileHash (Join-Path $sampleDir 'super.raw.img') -Algorithm SHA256).Hash.ToLower()
    if ($rawHash -ne $expected['super.raw.img']) { Fail "super.raw.img does not match the image SampleGen built" }
    Write-Host "super: 3 sparse parts expanded to raw, SHA-256 matches"

    # The same super cut into pieces the way a Qualcomm flash package ships it
    # (super_1.img ... with their sectors in rawprogram_unsparse0.xml): opening one piece
    # finds the others, and combining them gives the original image byte for byte.
    $qcPart = Join-Path $sampleDir 'qualcomm\super_1.img'
    if (Test-Path $qcPart) {
        Press 'images_close'
        Open-Image $qcPart
        $superList = Wait-For { By-Id $root 'images_partitions' } 30 "the super partition list (pieces)"
        Wait-For { (Rows $superList).Count -ge 3 } 60 "the super partitions (pieces)" | Out-Null
        Select-Item (Wait-For { By-Id $root 'images_details_tab' } 10 "the Details tab (pieces)")
        Start-Sleep -Milliseconds 500
        Save-Window ('{0:D2}-images-qualcomm-pieces' -f $i); $i++

        Press 'images_to_raw'
        $done = Wait-For { Find-Dialog } 120 "the pieces to be combined"
        Start-Sleep -Milliseconds 600
        Close-Dialog $done
        $combined = Join-Path (Split-Path $qcPart) 'super.raw.img'
        if (-not (Test-Path $combined)) { Fail "the combined super.raw.img was not written" }
        $combinedHash = (Get-FileHash $combined -Algorithm SHA256).Hash.ToLower()
        if ($combinedHash -ne $expected['super.raw.img']) { Fail "the combined pieces are not the original super" }
        Write-Host "super: Qualcomm pieces combined into the original image byte for byte"
    }
}

# ---------------------------------------------------------------- build super
# The size comes from the phone in fastboot; the layout from the sample super, whose
# partitions Image Tools unpacked above and the page finds by itself. Rebuilt raw, the
# result has to be the original image byte for byte.
if (Test-Path $superPart) {
    Go 'super_tab' | Out-Null
    Save-Window ('{0:D2}-super-empty' -f $i); $i++

    Press 'super_read_phone'
    $read = Wait-For { Find-Dialog } 30 "the size read from the phone"
    Start-Sleep -Milliseconds 600
    Close-Dialog $read
    $size = (By-Id $root 'super_size').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Write-Host "super size read from the phone: $size"
    if ($size -ne '9663676416') { Fail "the super size read from the phone is '$size'" }
    if ((Fastboot-Log 'getvar partition-size:super') -lt 1) { Fail "the size of super was not asked for" }

    Press 'super_import'
    Choose-File $superPart "the super import dialog"
    Start-Sleep -Seconds 2
    $unexpected = Find-Dialog
    if ($null -ne $unexpected) {
        $text = ($unexpected.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
        Save-Window ('{0:D2}-super-import-dialog' -f $i); $i++
        Fail "importing the sample super showed a dialog: $text"
    }
    $superRows = Wait-For { By-Id $root 'super_partitions' } 10 "the super partition list"
    Write-Host "super rows on screen after import: $((Rows $superRows).Count)"
    Wait-For { (Rows $superRows).Count -ge 3 } 20 "the imported partitions" | Out-Null
    $size = (By-Id $root 'super_size').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($size -ne '268435456') { Fail "importing the sample super set the size to '$size'" }
    Select-Item (Wait-For { By-Id $root 'super_format_raw' } 10 "the raw output choice")
    Save-Window ('{0:D2}-super-imported' -f $i); $i++

    $rebuilt = Join-Path $sampleDir 'super-rebuilt.img'
    Remove-Item $rebuilt -ErrorAction SilentlyContinue
    Press 'super_build'
    Choose-File $rebuilt "the super save dialog"
    $built = Wait-For { Find-Dialog } 240 "super to be built and checked"
    Start-Sleep -Milliseconds 800
    Save-Window ('{0:D2}-super-built' -f $i); $i++
    Close-Dialog $built
    if (-not (Test-Path $rebuilt)) { Fail "super-rebuilt.img was not written" }
    $rebuiltHash = (Get-FileHash $rebuilt -Algorithm SHA256).Hash.ToLower()
    if ($rebuiltHash -ne $expected['super.raw.img']) { Fail "the rebuilt super is not the original image byte for byte" }
    Write-Host "build super: layout imported, 5 unpacked images found, rebuilt super matches the original byte for byte"
}

# ---------------------------------------------------------------- terminal
# A command typed in the Terminal runs with the bundled fastboot and shows its output; one
# that erases a boot-chain partition asks for the name first, and answering No runs nothing.
function Terminal-Run([string] $line) {
    $box = Wait-For { Shown $root 'terminal_input' } 10 "the terminal line"
    $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($line)
    Press 'terminal_run'
}
function Terminal-Output {
    return (By-Id $root 'terminal_output').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}
Go 'terminal_tab' | Out-Null
Terminal-Run 'fastboot getvar product'
Wait-For { (Terminal-Output) -match 'product:sample_a64' -and (Terminal-Output) -match '\[exit code 0\]' } 30 "getvar in the terminal" | Out-Null
$erasesBefore = Fastboot-Log 'erase abl_a'
Terminal-Run 'fastboot erase abl_a'
$termAsk = Wait-For { Find-Dialog } 20 "the terminal's critical partition question"
Start-Sleep -Milliseconds 400
Answer-Dialog $termAsk 'No'
Wait-For { (Terminal-Output) -match 'Not run\.' } 10 "the terminal to say it did not run" | Out-Null
if ((Fastboot-Log 'erase abl_a') -ne $erasesBefore) { Fail "the terminal erased abl_a after No" }
Save-Window ('{0:D2}-terminal' -f $i); $i++
Write-Host "terminal: getvar ran, erase of abl_a asked first and did not run"

# ---------------------------------------------------------------- log and about
Go 'logs_tab' | Out-Null
Save-Window ('{0:D2}-logs' -f $i); $i++
Go 'about_tab' | Out-Null
Save-Window ('{0:D2}-about' -f $i); $i++

if (Test-Path $crashLog) { Fail "the app recorded an unhandled exception" }
if ($proc.HasExited) { Fail "app exited while being captured, code $($proc.ExitCode)" }
Stop-Process -Id $proc.Id -Force
Write-Host "captured $($i - 1) screens"

# ---------------------------------------------------------------- the same app in Arabic
# Right-to-left layout, Arabic strings and the Noto Kufi Arabic font, on the main pages.
if ($Arabic) {
    Start-Sleep -Seconds 2
    $env:FASTBOOT_STUDIO_LANG = 'ar'
    Launch-App

    Go 'payload_tab' | Out-Null
    Wait-For { Shown $root 'payload_info' } 60 "the payload to open (Arabic)" | Out-Null
    Save-Window 'ar-01-payload'

    Go 'device_tab' | Out-Null
    $devices = Wait-For { By-Id $root 'fastboot_devices_list' } 10 "the device list (Arabic)"
    $row = Wait-For { $devices.FindFirst($Scope::Descendants, $rowCond) } 20 "a device (Arabic)"
    Select-Item $row
    $row.SetFocus()
    Send '{ENTER}'
    Wait-For { Shown $root 'fastboot_info_list' } 20 "the device page (Arabic)" | Out-Null
    Start-Sleep -Seconds 3
    Save-Window 'ar-02-device'

    Go 'flash_tab' | Out-Null
    Save-Window 'ar-03-flash'

    # The phone in fastboot, then booted into a rooted Android: adb is the shell user there
    # and every partition read has to go through su.
    if ($ExpectedBackups -gt 0) { Set-Content $adbMode 'none' }
    Go 'backup_tab' | Out-Null
    if ($ExpectedBackups -gt 0) {
        Wait-For { Shown $root 'backup_fb_reboot_system' } 30 "the fastboot panel (Arabic)" | Out-Null
        Start-Sleep -Milliseconds 600
        Save-Window 'ar-09-backup-fastboot'
        Set-Content $adbMode 'android'
        Wait-For { $null -eq (Shown $root 'backup_fb_reboot_system') } 30 "the fastboot panel to go (Arabic)" | Out-Null
    }
    $adbList = Wait-For { By-Id $root 'backup_devices' } 10 "the adb device list (Arabic)"
    Wait-For { (Rows $adbList).Count -gt 0 } 30 "a device over adb (Arabic)" | Out-Null
    Select-Item (Wait-For { By-Id $root 'backup_files_tab' } 10 "the Files tab (Arabic)")
    $files = Wait-For { By-Id $root 'files_list' } 10 "the file list (Arabic)"
    Wait-For { (Rows $files).Count -ge 5 } 30 "the /sdcard listing (Arabic)" | Out-Null
    Save-Window 'ar-04-backup-files'
    Select-Item (Wait-For { By-Id $root 'backup_partitions_tab' } 10 "the Partitions tab (Arabic)")
    Press 'backup_read'
    $parts = Wait-For { By-Id $root 'backup_partition_list' } 10 "the partition list (Arabic)"
    Wait-For { (Rows $parts).Count -ge 3 } 60 "the partition table (Arabic)" | Out-Null
    Start-Sleep -Milliseconds 600
    Save-Window 'ar-05-backup-partitions'

    if ($ExpectedBackups -gt 0) {
        Start-Sleep -Seconds 1   # a new backup folder needs a newer timestamp than the first
        Press 'backup_start'
        $done = Wait-For { Find-Dialog } 240 "the backup to finish (Arabic, su)"
        Start-Sleep -Milliseconds 600
        Close-Dialog $done
        Check-Backup 'android with su'
        $viaSu = @(Get-Content $fakeAdbLog | Where-Object { $_ -match '^\S+\s+\[android\].*exec-out \| su -c' }).Count
        Write-Host "partitions read through su: $viaSu"
        if ($viaSu -ne $ExpectedBackups) { Fail "expected $ExpectedBackups partition reads through su, saw $viaSu" }
        Remove-Item $adbMode -ErrorAction SilentlyContinue
    }

    if (Test-Path $superPart) {
        Go 'images_tab' | Out-Null
        Open-Image $superPart
        $superList = Wait-For { By-Id $root 'images_partitions' } 30 "the super partition list (Arabic)"
        Wait-For { (Rows $superList).Count -ge 3 } 60 "the super partitions (Arabic)" | Out-Null
        Start-Sleep -Milliseconds 600
        Save-Window 'ar-08-images-super'
    }

    if (Test-Path $superPart) {
        Go 'super_tab' | Out-Null
        Press 'super_import'
        Choose-File $superPart "the super import dialog (Arabic)"
        $superRows = Wait-For { By-Id $root 'super_partitions' } 10 "the super partition list (Arabic)"
        Wait-For { (Rows $superRows).Count -ge 3 } 20 "the imported partitions (Arabic)" | Out-Null
        Start-Sleep -Milliseconds 600
        Save-Window 'ar-10-super'
    }

    # A dialog: switching back to English asks to restart. Answer "No" (the first button).
    Go 'about_tab' | Out-Null
    Save-Window 'ar-06-about'
    # The language buttons sit in a panel UI Automation does not see; find "English" by name.
    $byName = New-Object System.Windows.Automation.PropertyCondition($UIA::NameProperty, 'English')
    $english = Wait-For { $root.FindFirst($Scope::Descendants, $byName) } 10 "the English button"
    $english.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $ask = Wait-For { Find-Dialog } 10 "the restart question"
    Start-Sleep -Milliseconds 600
    Save-Window 'ar-07-dialog'
    Close-Dialog $ask

    if (Test-Path $crashLog) { Fail "the app recorded an unhandled exception in Arabic" }
    if ($proc.HasExited) { Fail "the Arabic app exited while being captured, code $($proc.ExitCode)" }
    Stop-Process -Id $proc.Id -Force
    Remove-Item Env:FASTBOOT_STUDIO_LANG
    Write-Host "captured the Arabic screens"
}

# ---------------------------------------------------------------- the light theme
# The same palette roles in light colours, chosen in About (here forced for this run).
Start-Sleep -Seconds 2
$env:FASTBOOT_STUDIO_THEME = 'light'
Launch-App
Go 'payload_tab' | Out-Null
Wait-For { Shown $root 'payload_info' } 60 "the payload to open (light)" | Out-Null
Save-Window 'light-01-payload'
Go 'device_tab' | Out-Null
$devices = Wait-For { By-Id $root 'fastboot_devices_list' } 10 "the device list (light)"
$row = Wait-For { $devices.FindFirst($Scope::Descendants, $rowCond) } 20 "a device (light)"
Select-Item $row
$row.SetFocus()
Send '{ENTER}'
Wait-For { Shown $root 'fastboot_info_list' } 20 "the device page (light)" | Out-Null
Start-Sleep -Seconds 3
Save-Window 'light-02-device'
Go 'flash_tab' | Out-Null
Save-Window 'light-03-flash'
Go 'terminal_tab' | Out-Null
Terminal-Run 'fastboot devices'
Wait-For { (Terminal-Output) -match 'FBE0SAMPLE01' } 30 "fastboot devices in the terminal (light)" | Out-Null
Save-Window 'light-04-terminal'
Go 'about_tab' | Out-Null
Save-Window 'light-05-about'
if (Test-Path $crashLog) { Fail "the app recorded an unhandled exception in the light theme" }
if ($proc.HasExited) { Fail "the light-theme app exited while being captured, code $($proc.ExitCode)" }
Stop-Process -Id $proc.Id -Force
Remove-Item Env:FASTBOOT_STUDIO_THEME
Write-Host "captured the light theme"
