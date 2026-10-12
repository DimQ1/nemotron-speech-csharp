# Layout check of the RUNNING VoiceType.Uno desktop head (Skia, Windows) —
# the same rules as the mockup check in render.ps1, applied through UI Automation:
#   - every visible button / toggle / combo box lies inside the window client area,
#   - hit target >= 44x44 px (at 100 % scale; scaled by the monitor DPI otherwise),
#   - no two interactive elements intersect,
#   - the Record button is horizontally centred (|dx| <= 2 px).
#
# Usage:
#   pwsh docs/design/voicetype-uno-mobile/uia-layout-check.ps1 [-Exe <path to VoiceType.Uno.exe>] [-Sizes 360x800,1280x800]
# The app is started, resized to each size, checked, and closed. Exit code 1 on any issue.

param(
    [string]$Exe = "$PSScriptRoot\..\..\..\apps\VoiceType.Uno\src\VoiceType.Uno\bin\Debug\net10.0-desktop\VoiceType.Uno.exe",
    [string[]]$Sizes = @('360x800', '320x568', '800x360', '480x560', '1280x800'),
    [string]$RecordName = 'Start recording',
    # Elements to invoke (by automation name) after each resize, before the check,
    # e.g. 'Open menu' or 'Open menu','Speech models'. Empty = main screen.
    [string[]]$Steps = @(),
    [switch]$NoRecord,
    # Names to skip — e.g. main-screen buttons covered by the open menu: UIA still
    # reports elements under the light-dismiss overlay although they cannot be tapped.
    [string[]]$Ignore = @()
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$Exe = (Resolve-Path $Exe).Path
$proc = Start-Process $Exe -PassThru
try {
    $deadline = (Get-Date).AddSeconds(60)
    while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
    if ($proc.MainWindowHandle -eq 0) { throw 'Main window did not appear' }
    $hwnd = $proc.MainWindowHandle
    # Wait until the page content is in the automation tree (Skia renders after the window appears).
    $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $btn = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
    while ((Get-Date) -lt $deadline -and $root.FindAll([Windows.Automation.TreeScope]::Descendants, $btn).Count -lt 2) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 2
    $Sizes = @($Sizes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $ignoreSet = @($Ignore | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

    $interactive = @(
        [Windows.Automation.ControlType]::Button, [Windows.Automation.ControlType]::CheckBox,
        [Windows.Automation.ControlType]::ComboBox, [Windows.Automation.ControlType]::RadioButton,
        [Windows.Automation.ControlType]::TabItem, [Windows.Automation.ControlType]::MenuItem)

    $failed = $false
    $report = [ordered]@{}
    foreach ($size in $Sizes) {
        [int]$w = $size.Split('x')[0]; [int]$h = $size.Split('x')[1]
        $scale = ([Win]::GetDpiForWindow($hwnd)) / 96.0
        # Size the CLIENT area to w x h logical px: add the frame measured from the current window.
        $wr = New-Object Win+RECT; $cr = New-Object Win+RECT
        [void][Win]::GetWindowRect($hwnd, [ref]$wr); [void][Win]::GetClientRect($hwnd, [ref]$cr)
        [int]$outerW = [Math]::Round($w * $scale) + (($wr.Right - $wr.Left) - $cr.Right)
        [int]$outerH = [Math]::Round($h * $scale) + (($wr.Bottom - $wr.Top) - $cr.Bottom)
        [void][Win]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, $outerW, $outerH, 0x0014)
        Start-Sleep -Seconds 2
        foreach ($step in @($Steps | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
            $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $step
            $target = [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
            if (-not $target) { throw "Step '$step' not found at $size" }
            $target.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 2
        }

        [void][Win]::GetClientRect($hwnd, [ref]$cr)
        $p = New-Object Win+POINT; [void][Win]::ClientToScreen($hwnd, [ref]$p)
        $client = @{ L = $p.X; T = $p.Y; R = $p.X + $cr.Right; B = $p.Y + $cr.Bottom }

        $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
        $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        $items = foreach ($e in $all) {
            $c = $e.Current
            if ($interactive -notcontains $c.ControlType -or $c.IsOffscreen) { continue }
            # Debug builds host the Uno Hot Design overlay; it is not part of the app UI.
            if ("$($c.Name)" -like 'Uno.UI.*' -or "$($c.AutomationId)" -like 'Uno.UI.*') { continue }
            if ($ignoreSet -contains $c.Name) { continue }
            $r = $c.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { continue }
            # Elements clipped by a scroll viewer report their full rect; keep only those
            # whose centre is inside the client area (reachable without scrolling now).
            [pscustomobject]@{ Name = if ($c.Name) { $c.Name } else { $c.AutomationId }; Type = $c.ControlType.ProgrammaticName; R = $r }
        }

        $issues = @()
        $min = 44 * $scale - 1
        foreach ($i in $items) {
            $r = $i.R
            if ($r.Left -lt $client.L - 1 -or $r.Top -lt $client.T - 1 -or $r.Right -gt $client.R + 1 -or $r.Bottom -gt $client.B + 1) {
                $issues += "$($i.Name): outside window [$([int]$r.Left),$([int]$r.Top),$([int]$r.Right),$([int]$r.Bottom)]"
            }
            if ($r.Width -lt $min -or $r.Height -lt $min) { $issues += "$($i.Name): $([int]($r.Width/$scale))x$([int]($r.Height/$scale)) < 44x44" }
        }
        for ($a = 0; $a -lt $items.Count; $a++) {
            for ($b = $a + 1; $b -lt $items.Count; $b++) {
                $x = [Windows.Rect]::Intersect($items[$a].R, $items[$b].R)
                if (-not $x.IsEmpty -and $x.Width -gt 1 -and $x.Height -gt 1) {
                    # A control nested in another (e.g. a ComboBox's inner button) is not an overlap.
                    if (-not ($items[$a].R.Contains($items[$b].R) -or $items[$b].R.Contains($items[$a].R))) {
                        $issues += "$($items[$a].Name) overlaps $($items[$b].Name)"
                    }
                }
            }
        }
        $rec = $items | Where-Object { $_.Name -eq $RecordName } | Select-Object -First 1
        if ($NoRecord) { }
        elseif ($rec) {
            $dx = ($rec.R.Left + $rec.R.Right) / 2 - ($client.L + $client.R) / 2
            if ([Math]::Abs($dx) -gt 2 * $scale) { $issues += "record not centred (dx=$([int]$dx))" }
        } else { $issues += "record button '$RecordName' not found" }

        $report[$size] = [ordered]@{ checked = @($items | ForEach-Object Name); issues = $issues }
        $status = if ($issues.Count -eq 0) { 'OK  ' } else { $failed = $true; 'FAIL' }
        Write-Host ("{0} {1,-9} {2} elements  {3}" -f $status, $size, $items.Count, ($issues -join '; '))
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot 'uia-layout-check.json')
    if ($failed) { $global:LASTEXITCODE = 1 }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
}
exit $global:LASTEXITCODE
