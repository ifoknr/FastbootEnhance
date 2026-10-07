# Launches the published app with a sample OTA and captures every tab, including the
# nested ones, through UI Automation. Used by the screenshots workflow; also handy
# locally:  pwsh .github/scripts/screenshots.ps1 -App publish\FastbootEnhance.exe -Payload sample\sample-ota.zip -Out shots
param(
    [Parameter(Mandatory)] [string] $App,
    [Parameter(Mandatory)] [string] $Payload,
    [Parameter(Mandatory)] [string] $Out
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
}
"@
[Win32]::SetProcessDPIAware() | Out-Null

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$App = (Resolve-Path $App).Path
$Payload = (Resolve-Path $Payload).Path

function Save-Desktop([string] $name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bmp.Save((Join-Path $Out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Save-Window([IntPtr] $hwnd, [string] $name) {
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($hwnd, [ref] $r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    # PW_RENDERFULLCONTENT (2) is needed for WPF, which draws through DirectX.
    $ok = [Win32]::PrintWindow($hwnd, $hdc, 2)
    $g.ReleaseHdc($hdc)
    if (-not $ok) {
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    }
    $path = Join-Path $Out "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $path (${w}x${h})"
}

function Find-All($parent, $controlType, $scope) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $parent.FindAll($scope, $cond)
}

function Select-Tab($tab) {
    $pattern = $tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
    Start-Sleep -Milliseconds 1200
}

function Slug([string] $text) {
    return (($text -replace '[^A-Za-z0-9]+', '-').Trim('-')).ToLowerInvariant()
}

Write-Host "launching $App `"$Payload`""
$proc = Start-Process -FilePath $App -ArgumentList "`"$Payload`"" -WorkingDirectory (Split-Path $App) -PassThru

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 1
    if ($proc.HasExited) { break }
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
}

if ($hwnd -eq [IntPtr]::Zero) {
    Save-Desktop "00-desktop-on-failure"
    if ($proc.HasExited) { throw "app exited during start-up with code $($proc.ExitCode)" }
    throw "app window never appeared"
}

$crashLog = Join-Path $env:TEMP 'FastbootEnhance\crash.log'

function Fail([string] $why) {
    Save-Desktop "00-desktop-on-failure"
    if (Test-Path $crashLog) { Write-Host "---- crash.log ----"; Get-Content $crashLog | Write-Host }
    throw $why
}

# Give the payload time to open and the device list its first refresh.
Start-Sleep -Seconds 8
if ($proc.HasExited) { Fail "app exited after start-up with code $($proc.ExitCode)" }

Save-Desktop "00-desktop"

# Look the window up by process rather than trusting the first handle we saw: WPF can
# replace its initial window handle while starting.
$byProcess = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$root = $null
for ($i = 0; $i -lt 20 -and $null -eq $root; $i++) {
    try {
        $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children, $byProcess)
    } catch {
        Write-Host "lookup attempt $i failed: $($_.Exception.Message)"
    }
    if ($null -eq $root) { Start-Sleep -Seconds 1 }
}
if ($null -eq $root) { Fail "no top-level window found for pid $($proc.Id)" }

$hwnd = [IntPtr]$root.Current.NativeWindowHandle
Write-Host "window: '$($root.Current.Name)' hwnd=$hwnd"
[Win32]::SetForegroundWindow($hwnd) | Out-Null

$mainTabs = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'main_tabs')))
if ($null -eq $mainTabs) { Fail "main_tabs not found in '$($root.Current.Name)'" }

$index = 1
$topTabs = Find-All $mainTabs ([System.Windows.Automation.ControlType]::TabItem) ([System.Windows.Automation.TreeScope]::Children)
foreach ($top in $topTabs) {
    $topName = Slug $top.Current.Name
    Select-Tab $top

    # The fastboot tab hides its inner tabs until a device is picked; skip what is not on screen.
    $nested = @(Find-All $top ([System.Windows.Automation.ControlType]::TabItem) ([System.Windows.Automation.TreeScope]::Descendants) |
        Where-Object { -not $_.Current.IsOffscreen })
    if ($nested.Count -eq 0) {
        Save-Window $hwnd ('{0:D2}-{1}' -f $index, $topName); $index++
        continue
    }

    foreach ($sub in $nested) {
        Select-Tab $sub
        Save-Window $hwnd ('{0:D2}-{1}-{2}' -f $index, $topName, (Slug $sub.Current.Name)); $index++
    }
}

if ($proc.HasExited) { throw "app exited while being captured, code $($proc.ExitCode)" }
Stop-Process -Id $proc.Id -Force
Write-Host "captured $($index - 1) screens"
