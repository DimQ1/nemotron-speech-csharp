# Captures the RUNNING VoiceType.Uno desktop head (Skia) at phone and desktop sizes
# into app-screenshots/<size>-<state>.png via PrintWindow — the window does not have
# to be on top. States: main screen, menu open, Models page.
#
# Usage: pwsh docs/design/voicetype-uno-mobile/app-screenshots.ps1 [-Exe <VoiceType.Uno.exe>]

param(
    [string]$Exe = "$PSScriptRoot\..\..\..\apps\VoiceType.Uno\src\VoiceType.Uno\bin\Debug\net10.0-desktop\VoiceType.Uno.exe",
    [string[]]$Sizes = @('360x800', '800x360', '1280x800')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Shot {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$out = Join-Path $PSScriptRoot 'app-screenshots'
New-Item -ItemType Directory -Force $out | Out-Null

function Invoke-ByName($hwnd, [string]$name) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name
    $el = [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) { throw "'$name' not found" }
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
}

function Save-Client($hwnd, [string]$file) {
    $wr = New-Object Shot+RECT; $cr = New-Object Shot+RECT; $p = New-Object Shot+POINT
    [void][Shot]::GetWindowRect($hwnd, [ref]$wr); [void][Shot]::GetClientRect($hwnd, [ref]$cr)
    [void][Shot]::ClientToScreen($hwnd, [ref]$p)
    $bmp = New-Object Drawing.Bitmap ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [void][Shot]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $client = $bmp.Clone([Drawing.Rectangle]::new($p.X - $wr.Left, $p.Y - $wr.Top, $cr.Right, $cr.Bottom), $bmp.PixelFormat)
    $client.Save($file, [Drawing.Imaging.ImageFormat]::Png); $client.Dispose(); $bmp.Dispose()
}

foreach ($size in $Sizes) {
    [int]$w = $size.Split('x')[0]; [int]$h = $size.Split('x')[1]
    foreach ($state in 'main', 'menu', 'models') {
        $proc = Start-Process (Resolve-Path $Exe).Path -PassThru
        try {
            $deadline = (Get-Date).AddSeconds(60)
            while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
            $hwnd = $proc.MainWindowHandle
            $btn = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
            while ((Get-Date) -lt $deadline -and [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindAll([Windows.Automation.TreeScope]::Descendants, $btn).Count -lt 2) { Start-Sleep -Milliseconds 500 }

            $scale = [Shot]::GetDpiForWindow($hwnd) / 96.0
            $wr = New-Object Shot+RECT; $cr = New-Object Shot+RECT
            [void][Shot]::GetWindowRect($hwnd, [ref]$wr); [void][Shot]::GetClientRect($hwnd, [ref]$cr)
            [int]$ow = [Math]::Round($w * $scale) + (($wr.Right - $wr.Left) - $cr.Right)
            [int]$oh = [Math]::Round($h * $scale) + (($wr.Bottom - $wr.Top) - $cr.Bottom)
            [void][Shot]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, $ow, $oh, 0x0014)
            Start-Sleep -Seconds 3

            if ($state -ne 'main') { Invoke-ByName $hwnd 'Open menu' }
            if ($state -eq 'models') { Invoke-ByName $hwnd 'Speech models' }
            Save-Client $hwnd (Join-Path $out "$size-$state.png")
            Write-Host "saved $size-$state.png"
        }
        finally { if (-not $proc.HasExited) { $proc.Kill() } }
    }
}
