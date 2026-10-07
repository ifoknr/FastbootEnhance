# Drives the published app through a whole session on Windows and captures each screen:
# payload inspection, picking a device, flashing a full OTA, the log, and About.
# With the stand-in fastboot.exe from tools/FastbootEnhance.FakeFastboot it doubles as an
# end-to-end test: it fails unless every partition of the sample OTA reaches "fastboot flash".
param(
    [Parameter(Mandatory)] [string] $App,
    [Parameter(Mandatory)] [string] $Payload,
    [Parameter(Mandatory)] [string] $Out,
    [int] $ExpectedFlashes = 0
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
$crashLog = Join-Path $env:TEMP 'FastbootEnhance\crash.log'
$fakeLog = Join-Path $appDir 'fake-fastboot.log'
Remove-Item $crashLog, $fakeLog -ErrorAction SilentlyContinue

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
        if ([IntPtr]$w.Current.NativeWindowHandle -ne $script:hwnd) { return $w }
    }
    foreach ($w in $script:root.FindAll($Scope::Children, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Window)))) {
        return $w
    }
    return $null
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

    # The app shows "operation completed" (or an error) when it is done.
    $done = Wait-For { Find-Dialog } 240 "flashing to finish"
    Start-Sleep -Milliseconds 800
    $message = ($done.FindAll($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($UIA::ControlTypeProperty, $Type::Text))) |
        ForEach-Object { $_.Current.Name }) -join ' | '
    Write-Host "dialog after flashing: $message"
    Save-Window ('{0:D2}-flash' -f $i); $i++
    Send '{ENTER}'
    Start-Sleep -Seconds 2

    $flashes = @(Get-Content $fakeLog | Where-Object { $_ -match ' flash ' }).Count
    Write-Host "fastboot flash commands issued: $flashes (expected $ExpectedFlashes)"
    if ($flashes -ne $ExpectedFlashes) { Fail "expected $ExpectedFlashes flash commands, saw $flashes" }
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
