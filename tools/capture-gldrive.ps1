#!/usr/bin/env pwsh
# capture-gldrive.ps1 — capture the running GlDrive dashboard window.
# Usage:
#   powershell -File tools/capture-gldrive.ps1 [-OutPath <path>] [-Tab <index>]
# -Tab is the 1-based index of the sidebar nav item to select before snapping
# (e.g. -Tab 3 = Downloads). Default is the currently-active tab.
# Outputs the saved PNG path on stdout.

param(
    [string]$OutPath = "$env:TEMP\gldrive-screenshot.png",
    [int]$Tab = 0
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public struct RECT { public int Left, Top, Right, Bottom; }
public class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
}
"@

$MOUSEEVENTF_LEFTDOWN = 0x0002
$MOUSEEVENTF_LEFTUP   = 0x0004

$proc = Get-Process -Name "GlDrive" -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like "*GlDrive*" } |
        Select-Object -First 1
if (-not $proc) { Write-Error "GlDrive Dashboard not running"; exit 1 }

$hwnd = $proc.MainWindowHandle
if ([Win32]::IsIconic($hwnd)) { [Win32]::ShowWindow($hwnd, 9) | Out-Null }
[Win32]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 300

$rect = New-Object RECT
[Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

# Optional: click sidebar nav item by 1-based index. Sidebar tabs are vertical
# strip on the left, each ~46px tall starting just below the 44px top bar.
# Group separator items (DASHBOARD/MEDIA/...) are tiny ~28px headers between
# clickable items, so we map the 14 selectable indices through hard-coded
# Y offsets matched empirically against the current layout.
if ($Tab -gt 0) {
    # Approx Y centers of each clickable nav item, relative to top of window
    # client area (after 44px top bar). 1-based indexing.
    $tabYOffsets = @(
        # 1=Overview, 2=Notifications, 3=Wishlist, 4=Downloads
        120, 158, 196, 234,
        # group separator skipped
        # 5=Search, 6=Player, 7=Upcoming
        304, 342, 380,
        # group skipped
        # 8=IRC, 9=PreDB
        450, 488,
        # group skipped
        # 10=Spread, 11=Browse
        558, 596,
        # group skipped
        # 12=Mounts, 13=World Monitor, 14=Discord, 15=Streems, 16=AI Agent
        666, 704, 742, 780, 818
    )
    if ($Tab -ge 1 -and $Tab -le $tabYOffsets.Count) {
        $clickX = $rect.Left + 90
        $clickY = $rect.Top + $tabYOffsets[$Tab - 1]
        [Win32]::SetCursorPos($clickX, $clickY) | Out-Null
        Start-Sleep -Milliseconds 80
        [Win32]::mouse_event($MOUSEEVENTF_LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 30
        [Win32]::mouse_event($MOUSEEVENTF_LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 400
    }
}

# Re-read bounds in case the window moved
[Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

Write-Output $OutPath
