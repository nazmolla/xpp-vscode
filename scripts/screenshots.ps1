# Captures the feature screenshots: launches a throw-away VS Code window with only this extension, lets test/screenshots.js
# drive it, captures the whole window at each step into %TEMP%\xpp-shots-raw and crops it into docs/images with the
# rectangles in scripts/screenshots-crops.json (shots without one keep the window minus title, activity and status bar).
# Usage: powershell -ExecutionPolicy Bypass -File scripts/screenshots.ps1 [-Workspace <.code-workspace>] [-CropOnly]
#   XPP_SHOTS_ONLY=name1,name2 limits the run to those steps. -CropOnly re-crops the raw captures without VS Code.
# A step writes its name to step.txt, or JSON: {"name":..., "keys":[SendKeys strings typed first], "wait":ms, "capture":false}.
param([int]$Width = 1600, [int]$Height = 1000, [string]$Workspace = '', [switch]$CropOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$shots = Join-Path $env:TEMP 'xpp-shots'
$raw = Join-Path $env:TEMP 'xpp-shots-raw'
$profile = Join-Path $env:TEMP 'xpp-shots-profile'
$images = Join-Path $root 'docs\images'
$cropsFile = Join-Path $PSScriptRoot 'screenshots-crops.json'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Save-Crop([string]$source, [string]$target, $rect) {
    $bmp = [System.Drawing.Bitmap]::FromFile($source)
    try {
        $r = if ($rect) { New-Object System.Drawing.Rectangle $rect[0], $rect[1], $rect[2], $rect[3] }
             else { New-Object System.Drawing.Rectangle 48, 35, ($bmp.Width - 48), ($bmp.Height - 35 - 22) }
        $r = [System.Drawing.Rectangle]::Intersect($r, (New-Object System.Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height))
        $out = $bmp.Clone($r, $bmp.PixelFormat)
        $out.Save((Join-Path $images "$target.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $out.Dispose()
    } finally {
        $bmp.Dispose()
    }
}

# Crops one raw capture into its image, plus every image the crops file derives from it ("from").
function Save-Images([string]$name) {
    $crops = if (Test-Path $cropsFile) { Get-Content $cropsFile -Raw | ConvertFrom-Json } else { New-Object psobject }
    $source = Join-Path $raw "$name.png"
    if (-not (Test-Path $source)) { return }
    $own = $crops.PSObject.Properties[$name]
    # Without a crop rectangle the capture stays in the raw folder for review.
    if ($own -and -not $own.Value.from) { Save-Crop $source $name ($own.Value.rect) }
    foreach ($p in $crops.PSObject.Properties | Where-Object { $_.Value.from -eq $name }) { Save-Crop $source $p.Name $p.Value.rect }
}

New-Item -ItemType Directory -Force $raw, $images | Out-Null
if ($CropOnly) {
    Get-ChildItem $raw -Filter *.png | ForEach-Object { Save-Images $_.BaseName; Write-Host "cropped $($_.BaseName)" }
    return
}
Remove-Item $shots -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $shots, (Join-Path $profile 'User') | Out-Null

# Clean window: no welcome page, no chat, dark theme.
@'
{
  "workbench.startupEditor": "none",
  "onboarding.enabled": false,
  "workbench.welcomePage.walkthroughs.openOnInstall": false,
  "workbench.secondarySideBar.defaultVisibility": "hidden",
  "chat.commandCenter.enabled": false,
  "workbench.colorTheme": "Default Dark Modern",
  "window.restoreWindows": "none",
  "editor.minimap.enabled": false,
  "workbench.tips.enabled": false,
  "update.mode": "none",
  "extensions.autoUpdate": false,
  "extensions.autoCheckUpdates": false,
  "telemetry.telemetryLevel": "off",
  "git.openRepositoryInParentFolders": "never",
  "security.workspace.trust.enabled": false
}
'@ | Set-Content -Path (Join-Path $profile 'User\settings.json') -Encoding UTF8

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$env:XPP_SHOTS_DIR = $shots
$code = (Get-Command code.cmd).Source
$arguments = @("--extensionDevelopmentPath=`"$root`"", "--extensionTestsPath=`"$root\test\screenshots.js`"", "--user-data-dir=`"$profile`"", "--extensions-dir=`"$profile\extensions`"", '--disable-extensions', '--disable-workspace-trust', '--new-window')
if ($Workspace) { $arguments += "`"$Workspace`"" }
$proc = Start-Process -FilePath $code -PassThru -ArgumentList $arguments

function Find-Window {
    Get-Process Code -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*Extension Development Host*' } | Select-Object -First 1
}

function Click([IntPtr]$h, [double]$fx, [double]$fy) {
    $r = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$r) | Out-Null
    [Win]::SetCursorPos($r.Left + [int](($r.Right - $r.Left) * $fx), $r.Top + [int](($r.Bottom - $r.Top) * $fy)) | Out-Null
    [Win]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); [Win]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1500
}

# Drags with the left button between two points in window coordinates (e.g. a sash to widen the sidebar).
function Drag([IntPtr]$h, [int]$x1, [int]$y1, [int]$x2, [int]$y2) {
    $r = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$r) | Out-Null
    [Win]::SetCursorPos($r.Left + $x1, $r.Top + $y1) | Out-Null
    Start-Sleep -Milliseconds 300
    [Win]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
    for ($i = 1; $i -le 10; $i++) {
        [Win]::SetCursorPos($r.Left + $x1 + [int](($x2 - $x1) * $i / 10), $r.Top + $y1 + [int](($y2 - $y1) * $i / 10)) | Out-Null
        Start-Sleep -Milliseconds 50
    }
    [Win]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
    [Win]::SetCursorPos($r.Right - 5, $r.Bottom - 5) | Out-Null
    Start-Sleep -Milliseconds 800
}

$deadline = (Get-Date).AddMinutes(45)
$last = ''
$seen = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($seen -and -not (Find-Window)) { Write-Host 'test window closed'; break }
    $stepFile = Join-Path $shots 'step.txt'
    if (-not (Test-Path $stepFile)) { continue }
    $text = (Get-Content $stepFile -Raw).Trim()
    if ($text -eq $last) { continue }
    if ($text -eq 'finished') { break }
    $step = if ($text.StartsWith('{')) { $text | ConvertFrom-Json } else { [pscustomobject]@{ name = $text } }
    $name = $step.name
    $window = Find-Window
    if (-not $window) { continue }
    $seen = $true
    $h = $window.MainWindowHandle
    # Moving or re-focusing the window closes hovers, so only when it isn't in place already.
    $pos = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$pos) | Out-Null
    if ([Win]::GetForegroundWindow() -ne $h -or $pos.Left -ne 0 -or $pos.Top -ne 0 -or ($pos.Right - $pos.Left) -ne $Width -or ($pos.Bottom - $pos.Top) -ne $Height) {
        [Win]::ShowWindow($h, 9) | Out-Null
        [Win]::MoveWindow($h, 0, 0, $Width, $Height, $true) | Out-Null
        [Win]::SetForegroundWindow($h) | Out-Null
        Start-Sleep -Milliseconds 1500
    }
    if ($name -eq 'dismiss') {
        # First-run "Welcome to VS Code" sign-in dialog: "Continue without Signing In", then the next page's close button.
        Click $h 0.72 0.746
        Click $h 0.781 0.241
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 800
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    } else {
        if ($step.drag) { $d = @($step.drag); Drag $h $d[0] $d[1] $d[2] $d[3] }
        foreach ($k in @($step.keys | Where-Object { $_ })) {
            [System.Windows.Forms.SendKeys]::SendWait($k)
            Start-Sleep -Milliseconds 700
        }
        if ($step.wait) { Start-Sleep -Milliseconds ([int]$step.wait) }
        if ($step.capture -ne $false) {
            $r = New-Object Win+RECT
            [Win]::GetWindowRect($h, [ref]$r) | Out-Null
            $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $hdc = $g.GetHdc()
            [Win]::PrintWindow($h, $hdc, 2) | Out-Null
            $g.ReleaseHdc($hdc)
            $g.Dispose()
            $bmp.Save((Join-Path $raw "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
            Save-Images $name
            Write-Host "captured $name"
        }
    }
    New-Item -ItemType File -Force (Join-Path $shots "$name.done") | Out-Null
    $last = $text
}
Get-Content (Join-Path $shots 'log.txt') -ErrorAction SilentlyContinue
