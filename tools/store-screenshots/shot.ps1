# Store screenshot helpers for the running VoiceType window (PrintWindow, so the window need not be on top).
#   . .\shot.ps1; $h = Get-VoiceTypeWindow; Set-ClientSize $h 1920 1080; Save-Client $h out.png; Invoke-ByName $h 'History'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('StoreShot' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StoreShot {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@
}
[void][StoreShot]::SetProcessDpiAwarenessContext([IntPtr]-4)  # per-monitor v2: real pixels

function Get-VoiceTypeWindow {
    for ($i = 0; $i -lt 60; $i++) {
        $p = Get-Process VoiceType.Uno -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($p) { return $p.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    throw 'VoiceType window not found'
}

# Client area of exactly $w x $h pixels at ($x, $y) (default: the 100 % monitor left of the primary one).
function Set-ClientSize($hwnd, [int]$w, [int]$h, [int]$x = -2400, [int]$y = 80) {
    for ($i = 0; $i -lt 4; $i++) {
        $wr = New-Object StoreShot+RECT; $cr = New-Object StoreShot+RECT
        [void][StoreShot]::GetWindowRect($hwnd, [ref]$wr); [void][StoreShot]::GetClientRect($hwnd, [ref]$cr)
        $fw = $w + (($wr.Right - $wr.Left) - $cr.Right); $fh = $h + (($wr.Bottom - $wr.Top) - $cr.Bottom)
        [void][StoreShot]::SetWindowPos($hwnd, [IntPtr]::Zero, $x, $y, $fw, $fh, 0x0014)
        Start-Sleep -Milliseconds 600
        [void][StoreShot]::GetClientRect($hwnd, [ref]$cr)
        if ($cr.Right -eq $w -and $cr.Bottom -eq $h) { return }
    }
    Write-Warning "client is $($cr.Right)x$($cr.Bottom), wanted ${w}x$h"
}

function Save-Client($hwnd, [string]$file) {
    $wr = New-Object StoreShot+RECT; $cr = New-Object StoreShot+RECT; $p = New-Object StoreShot+POINT
    [void][StoreShot]::GetWindowRect($hwnd, [ref]$wr); [void][StoreShot]::GetClientRect($hwnd, [ref]$cr)
    [void][StoreShot]::ClientToScreen($hwnd, [ref]$p)
    $bmp = New-Object Drawing.Bitmap ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [void][StoreShot]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $client = $bmp.Clone([Drawing.Rectangle]::new($p.X - $wr.Left, $p.Y - $wr.Top, $cr.Right, $cr.Bottom), $bmp.PixelFormat)
    $client.Save($file, [Drawing.Imaging.ImageFormat]::Png); $client.Dispose(); $bmp.Dispose()
    "$file ($($cr.Right)x$($cr.Bottom))"
}

function Find-ByName($hwnd, [string]$name, [int]$tries = 8) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name
    for ($t = 0; $t -lt $tries; $t++) {
        $el = [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 600
    }
    return $null
}

function Invoke-ByName($hwnd, [string]$name, [int]$wait = 1500) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name
    for ($t = 0; $t -lt 8; $t++) {
        # The first element of that name that can be clicked (a tooltip or a text of the same name comes first in the tree).
        foreach ($el in [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindAll([Windows.Automation.TreeScope]::Descendants, $cond)) {
            $pattern = $null
            if ($el.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
            elseif ($el.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select() }
            elseif ($el.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
            else { continue }
            Start-Sleep -Milliseconds $wait
            return
        }
        Start-Sleep -Milliseconds 600
    }
    throw "'$name' not found (or not clickable)"
}

# Names of the visible elements (to find what to click).
function Get-Names($hwnd, [int]$max = 200) {
    $all = [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $all | Where-Object { $_.Current.Name } | Select-Object -First $max | ForEach-Object { '{0} [{1}]' -f $_.Current.Name, $_.Current.ControlType.ProgrammaticName.Replace('ControlType.', '') }
}
