# Captures the README screenshots: launches a throw-away VS Code window with only this extension, no project selected
# (standard objects only), lets test/screenshots.js drive it, and captures the window at each step into docs/images.
# Usage: powershell -ExecutionPolicy Bypass -File scripts/screenshots.ps1
param([int]$Width = 1600, [int]$Height = 1000)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$shots = Join-Path $env:TEMP 'xpp-shots'
$profile = Join-Path $env:TEMP 'xpp-shots-profile'
$images = Join-Path $root 'docs\images'
Remove-Item $shots -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $shots, $images, (Join-Path $profile 'User') | Out-Null

# Clean window: no welcome page, no chat, no project, dark theme.
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
  "telemetry.telemetryLevel": "off",
  "xpp.project": ""
}
'@ | Set-Content -Path (Join-Path $profile 'User\settings.json') -Encoding UTF8

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -AssemblyName System.Drawing

$env:XPP_SHOTS_DIR = $shots
$code = (Get-Command code.cmd).Source
$proc = Start-Process -FilePath $code -PassThru -ArgumentList "--extensionDevelopmentPath=`"$root`"", "--extensionTestsPath=`"$root\test\screenshots.js`"", "--user-data-dir=`"$profile`"", '--disable-extensions', '--new-window'

function Find-Window {
    Get-Process Code -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*Extension Development Host*' } | Select-Object -First 1
}

$deadline = (Get-Date).AddMinutes(15)
$last = ''
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    $stepFile = Join-Path $shots 'step.txt'
    if (-not (Test-Path $stepFile)) { continue }
    $step = (Get-Content $stepFile -Raw).Trim()
    if ($step -eq $last) { continue }
    if ($step -eq 'finished') { break }
    $window = Find-Window
    if (-not $window) { continue }
    $h = $window.MainWindowHandle
    [Win]::ShowWindow($h, 9) | Out-Null
    [Win]::MoveWindow($h, 0, 0, $Width, $Height, $true) | Out-Null
    [Win]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 1500
    if ($step -eq 'dismiss') {
        # First-run "Welcome to VS Code" sign-in dialog: "Continue without Signing In" (bottom right of the dialog), then Escape.
        $r = New-Object Win+RECT
        [Win]::GetWindowRect($h, [ref]$r) | Out-Null
        [Win]::SetCursorPos($r.Left + [int](($r.Right - $r.Left) * 0.72), $r.Top + [int](($r.Bottom - $r.Top) * 0.746)) | Out-Null
        [Win]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); [Win]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 1500
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 800
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        New-Item -ItemType File -Force (Join-Path $shots "$step.done") | Out-Null
        $last = $step
        continue
    }
    $r = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.Right - $r.Left), ($r.Bottom - $r.Top)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [Win]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save((Join-Path $images "$step.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "captured $step"
    New-Item -ItemType File -Force (Join-Path $shots "$step.done") | Out-Null
    $last = $step
}
Get-Content (Join-Path $shots 'log.txt') -ErrorAction SilentlyContinue
