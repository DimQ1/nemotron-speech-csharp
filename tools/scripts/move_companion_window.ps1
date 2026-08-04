# Move the companion window to given desktop coordinates
param([int]$X = 1400, [int]$Y = 400)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WinMove {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lp);
}
"@

$script:found = [IntPtr]::Zero
$cb = {
    param($h, $lp)
    $sb = New-Object System.Text.StringBuilder 256
    [WinMove]::GetWindowTextW($h, $sb, 256) | Out-Null
    if ($sb.ToString() -eq "LVA Companion") { $script:found = $h; return $false }
    return $true
}
[WinMove]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null

if ($script:found -ne [IntPtr]::Zero) {
    [WinMove]::SetWindowPos($script:found, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001) | Out-Null
    Write-Output "moved companion to ($X,$Y)"
} else {
    Write-Output "companion window not found"
}
