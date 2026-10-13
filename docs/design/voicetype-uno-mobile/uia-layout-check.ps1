# Layout check of the RUNNING VoiceType.Uno desktop head (Skia, Windows) —
# the same rules as the mockup check in render.ps1, applied through UI Automation:
#   - every visible button / toggle / combo box / radio button lies inside the window client area,
#   - hit target >= 44x44 px (at 100 % scale; scaled by the monitor DPI otherwise),
#   - no two interactive elements intersect,
#   - the Record button is horizontally centred (|dx| <= 2 px)      [main screen only; -NoRecord skips it].
#
# Usage:
#   pwsh docs/design/voicetype-uno-mobile/uia-layout-check.ps1 [-Exe <path to VoiceType.Uno.exe>] [-Sizes 360x800,1280x800]
#                                                              [-Scenario main|settings-hub|settings-storage|...] [-ScrollSweep] [-ShotDir <dir>]
# The app is started, resized to each size, checked, and closed. Exit code 1 on any issue.
#
# Settings scenarios (docs/design/voicetype-uno-settings §5): -Scenario settings-hub | settings-storage | settings-speech |
#   settings-audio | settings-translation | settings-typing | settings-hotkeys | settings-general. They open the menu, then Settings,
#   then the section (under 840 px the section replaces the list; from 840 px the list stays on the left). Add -ScrollSweep to
#   check each screen at scroll 0 / 50 / 100 %, and -ShotDir to save a PrintWindow screenshot per size (the window does not have
#   to be on top).
#
# The app runs against a throwaway DEBUG data root (VOICETYPE_DATA_ROOT) so a check never reads or moves the real
# %LOCALAPPDATA%\VoiceType. -Seed fills it with fake models, recordings and chats so the Storage page has something to show.
# -Env sets more variables for the run, e.g. -Env @{ VOICETYPE_SETTINGS_ANDROID = '1' } previews the phone layout (no folder
# buttons, no desktop-only sections) on the desktop head.

param(
    [string]$Exe = "$PSScriptRoot\..\..\..\apps\VoiceType.Uno\src\VoiceType.Uno\bin\Debug\net10.0-desktop\VoiceType.Uno.exe",
    [string[]]$Sizes = @('360x800', '320x568', '800x360', '480x560', '1280x800'),
    [string]$RecordName = 'Start recording',
    # Elements to invoke (by automation name) after each resize, before the check,
    # e.g. 'Open menu' or 'Open menu','Speech models'. Empty = main screen.
    [string[]]$Steps = @(),
    # A named screen (see above); overrides -Steps.
    [string]$Scenario = '',
    [switch]$NoRecord,
    [switch]$ScrollSweep,
    [string]$ShotDir = '',
    # Sandbox data root for the run (default: a new folder under %TEMP%).
    [string]$DataRoot = '',
    [switch]$Seed,
    # Names the scenario variant in the report and the screenshots ("settings-storage+move"). With -Seed, 'incomplete' also seeds the
    # state after a start-up move that did not finish (the yellow notes). A pending move cannot be seeded: the next start would
    # carry it out, so reach it with -ExtraSteps (below).
    [string]$State = '',
    # Names to invoke after the scenario steps, e.g. -ExtraSteps 'Change the Recordings folder' leaves the "Move recordings to the
    # new folder?" question open for the check (the dialog is part of the checked screen); add 'Move on restart' to reach the pending
    # note. The DEBUG-only variable VOICETYPE_SETTINGS_PICK=<folder> (-Env) stands in for the system folder picker.
    [string[]]$ExtraSteps = @(),
    [hashtable]$Env = @{},
    # Names to skip — e.g. main-screen buttons covered by the open menu: UIA still
    # reports elements under the light-dismiss overlay although they cannot be tapped.
    [string[]]$Ignore = @()
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@

$sections = @{
    'storage' = 'Storage'; 'speech' = 'Speech recognition'; 'audio' = 'Audio input'; 'translation' = 'Translation and Gemma'
    'typing' = 'Typing into apps'; 'hotkeys' = 'Keyboard shortcuts'; 'general' = 'General'
}
if ($Scenario -and $Scenario -ne 'main') {
    if ($Scenario -eq 'settings-hub') { $Steps = @('Open menu', 'Settings') }
    elseif ($Scenario -match '^settings-(\w+)$' -and $sections.ContainsKey($Matches[1])) { $Steps = @('Open menu', 'Settings', $sections[$Matches[1]]) }
    else { throw "Unknown scenario '$Scenario'" }
    $NoRecord = $true
}
if ($Scenario -eq 'main') { $Steps = @() }

$ownSandbox = -not $DataRoot
if (-not $DataRoot) { $DataRoot = Join-Path ([IO.Path]::GetTempPath()) ("voicetype-uia-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
New-Item -ItemType Directory -Force "$DataRoot\models" | Out-Null
if ($Seed) {
    $m = "$DataRoot\models"
    New-Item -ItemType Directory -Force "$m\asr\parakeet-tdt-0.6b-v3-onnx-int4", "$m\llm", "$DataRoot\chats" | Out-Null
    Set-Content "$m\asr\parakeet-tdt-0.6b-v3-onnx-int4\genai_config.json" '{"decoder":{"filename":"decoder.onnx"}}'
    # Placeholder files, not real models; NTFS does not make them sparse, so keep them small (the sandbox is deleted afterwards).
    foreach ($f in @(@("$m\asr\parakeet-tdt-0.6b-v3-onnx-int4\decoder.onnx", 120MB), @("$m\llm\gemma-4-E2B-it.litertlm", 300MB))) {
        $s = [IO.File]::Create($f[0]); $s.SetLength($f[1]); $s.Close()
    }
    foreach ($i in 1..3) {
        $d = "$DataRoot\sessions\2026-10-0${i}_120000_abc$i"
        New-Item -ItemType Directory -Force $d | Out-Null
        $s = [IO.File]::Create("$d\audio.opus"); $s.SetLength(5MB); $s.Close()
    }
    if (-not (Test-Path "$DataRoot\settings.json")) {
        $extra = ''
        if ($State -eq 'incomplete') {
            $extra = ',"ModelStorageNote":"Models were not moved to E:\\Models: it is on another drive and models are not copied automatically. They stay in the model storage folder.","SessionsStorageNote":"Recordings were not moved to E:\\Recordings: it is on another drive and recordings are not copied automatically. They stay in the old folder."'
            Set-Content "$DataRoot\data-location.note.txt" 'App data was not moved to D:\VoiceType: some files were in use. It stays in the current folder.'
        }
        Set-Content "$DataRoot\settings.json" ('{"SelectedModel":"parakeet-tdt-0.6b-v3-onnx-int4","TranslationEnabled":true,"AudioSource":"Mix"' + $extra + '}')
    }
}
$env:VOICETYPE_DATA_ROOT = $DataRoot
foreach ($k in $Env.Keys) { Set-Item "Env:\$k" $Env[$k] }
if ($ShotDir) { New-Item -ItemType Directory -Force $ShotDir | Out-Null }

function Save-Client($hwnd, [string]$file) {
    $wr = New-Object Win+RECT; $cr = New-Object Win+RECT; $p = New-Object Win+POINT
    [void][Win]::GetWindowRect($hwnd, [ref]$wr); [void][Win]::GetClientRect($hwnd, [ref]$cr); [void][Win]::ClientToScreen($hwnd, [ref]$p)
    $bmp = New-Object Drawing.Bitmap ($wr.Right - $wr.Left), ($wr.Bottom - $wr.Top)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc(); [void][Win]::PrintWindow($hwnd, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose()
    $client = $bmp.Clone([Drawing.Rectangle]::new($p.X - $wr.Left, $p.Y - $wr.Top, $cr.Right, $cr.Bottom), $bmp.PixelFormat)
    $client.Save($file, [Drawing.Imaging.ImageFormat]::Png); $client.Dispose(); $bmp.Dispose()
}

$Exe = (Resolve-Path $Exe).Path
$proc = Start-Process $Exe -PassThru
try {
    $deadline = (Get-Date).AddSeconds(60)
    while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
    if ($proc.MainWindowHandle -eq 0) { throw 'Main window did not appear' }
    $hwnd = $proc.MainWindowHandle
    # Wait until the page content is in the automation tree (Skia renders after the window appears).
    $btn = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
    while ((Get-Date) -lt $deadline) {
        try {
            $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
            if ($root.FindAll([Windows.Automation.TreeScope]::Descendants, $btn).Count -ge 2) { break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 4
    $Sizes = @($Sizes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $ignoreSet = @($Ignore | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

    $interactive = @(
        [Windows.Automation.ControlType]::Button, [Windows.Automation.ControlType]::CheckBox,
        [Windows.Automation.ControlType]::ComboBox, [Windows.Automation.ControlType]::RadioButton,
        [Windows.Automation.ControlType]::TabItem, [Windows.Automation.ControlType]::MenuItem,
        [Windows.Automation.ControlType]::Edit)

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
        foreach ($step in @(@($Steps) + @($ExtraSteps) | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
            $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $step
            $target = $null
            for ($try = 0; $try -lt 6 -and -not $target; $try++) {
                $target = [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
                if (-not $target) { Start-Sleep -Milliseconds 700 }
            }
            if (-not $target) { throw "Step '$step' not found at $size" }
            $pat = $null
            if ($target.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() }
            elseif ($target.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
            else { throw "Step '$step' has no Invoke/SelectionItem pattern" }
            Start-Sleep -Seconds 2
        }

        $allIssues = @(); $allNames = @()
        $fracs = if ($ScrollSweep) { @(0, 0.5, 1) } else { @(0) }
        foreach ($frac in $fracs) {
            $rootE = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
            $sp = $null; $spR = $null
            if ($ScrollSweep) {
                foreach ($cand in $rootE.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
                    $o = $null
                    if ($cand.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern, [ref]$o) -and $o.Current.VerticallyScrollable -and $cand.Current.BoundingRectangle.Height -gt 200) { $sp = $o; $spR = $cand.Current.BoundingRectangle }
                }
                if ($sp) { $sp.SetScrollPercent(-1, $frac * 100); Start-Sleep -Seconds 1 }
                elseif ($frac -gt 0) { continue }
            }
            [void][Win]::GetClientRect($hwnd, [ref]$cr)
            $p = New-Object Win+POINT; [void][Win]::ClientToScreen($hwnd, [ref]$p)
            $client = @{ L = $p.X; T = $p.Y; R = $p.X + $cr.Right; B = $p.Y + $cr.Bottom }

            $root = [Windows.Automation.AutomationElement]::FromHandle($hwnd)
            # An open dialog or flyout (a "Popup" window in the automation tree) is the only thing that can be tapped: UIA still lists the
            # page behind its light-dismiss overlay, which would report overlaps that do not exist.
            $popupCond = New-Object Windows.Automation.AndCondition (
                (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Window)),
                (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), 'Popup'))
            $buttonCond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
            $popup = $null
            foreach ($cand in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $popupCond)) {
                # Empty popups (tooltips) and the Uno Hot Design overlay of Debug builds are not dialogs: a dialog has app buttons.
                if ($popup) { continue }
                foreach ($b in $cand.FindAll([Windows.Automation.TreeScope]::Descendants, $buttonCond)) {
                    if ("$($b.Current.Name)" -notlike 'Uno.UI.*' -and "$($b.Current.AutomationId)" -notlike 'Uno.UI.*') { $popup = $cand; break }
                }
            }
            $scope = if ($popup) { $popup } else { $root }
            $all = $scope.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
            $items = foreach ($e in $all) {
                $c = $e.Current
                if ($interactive -notcontains $c.ControlType -or $c.IsOffscreen) { continue }
                # Debug builds host the Uno Hot Design overlay; it is not part of the app UI.
                if ("$($c.Name)" -like 'Uno.UI.*' -or "$($c.AutomationId)" -like 'Uno.UI.*') { continue }
                if ($ignoreSet -contains $c.Name) { continue }
                $r = $c.BoundingRectangle
                if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { continue }
                [pscustomobject]@{ Name = if ($c.Name) { $c.Name } else { $c.AutomationId }; Type = $c.ControlType.ProgrammaticName; R = $r }
            }

            $issues = @()
            $min = 44 * $scale - 1
            foreach ($i in $items) {
                $r = $i.R
                if ($r.Left -lt $client.L - 1 -or $r.Top -lt $client.T - 1 -or $r.Right -gt $client.R + 1 -or $r.Bottom -gt $client.B + 1) {
                    $issues += "$($i.Name): outside window [$([int]$r.Left),$([int]$r.Top),$([int]$r.Right),$([int]$r.Bottom)]"
                }
                # Elements clipped by a scroll viewer report their visible part only: not a size problem.
                $clipped = $spR -and ($r.Top -le $spR.Top + 2 -or $r.Bottom -ge $spR.Bottom - 2)
                if (($r.Width -lt $min -or $r.Height -lt $min) -and -not $clipped) { $issues += "$($i.Name): $([int]($r.Width/$scale))x$([int]($r.Height/$scale)) < 44x44" }
            }
            for ($a = 0; $a -lt $items.Count; $a++) {
                for ($b = $a + 1; $b -lt $items.Count; $b++) {
                    $x = [Windows.Rect]::Intersect($items[$a].R, $items[$b].R)
                    if (-not $x.IsEmpty -and $x.Width -gt 1 -and $x.Height -gt 1) {
                        # A control nested in another (e.g. a ComboBox's inner button, a chip in a key button) is not an overlap.
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

            $allIssues += @($issues | ForEach-Object { if ($ScrollSweep) { "[scroll $frac] $_" } else { $_ } })
            $allNames += @($items | ForEach-Object Name)
            if ($ShotDir) { Save-Client $hwnd (Join-Path $ShotDir ("$(if ($Scenario) { $Scenario } else { 'screen' })$(if ($State) { "+$State" })-$size" + $(if ($ScrollSweep) { "-s$([int]($frac*100))" } else { '' }) + '.png')) }
        }
        $issues = @($allIssues | Select-Object -Unique)
        $names = @($allNames | Select-Object -Unique)
        $report[$size] = [ordered]@{ checked = $names; issues = $issues }
        $status = if ($issues.Count -eq 0) { 'OK  ' } else { $failed = $true; 'FAIL' }
        Write-Host ("{0} {1,-9} {2} elements  {3}" -f $status, $size, $names.Count, ($issues -join '; '))
    }
    if ($Scenario -and $Scenario -ne 'main') {
        # Settings scenarios are collected, keyed "<scenario> <size>", in one report next to the settings design.
        $file = Join-Path $PSScriptRoot '..\voicetype-uno-settings\uia-layout-check.json'
        $merged = [ordered]@{}
        if (Test-Path $file) { (Get-Content $file -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $merged[$_.Name] = $_.Value } }
        foreach ($k in $report.Keys) { $merged["$Scenario$(if ($State) { "+$State" }) $k"] = $report[$k] }
        $merged | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 $file
    }
    else {
        $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $PSScriptRoot 'uia-layout-check.json')
    }
    if ($failed) { $global:LASTEXITCODE = 1 }
}
finally {
    if (-not $proc.HasExited) { $proc.Kill() }
    foreach ($k in $Env.Keys) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue }
    if ($ownSandbox) { Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath $DataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $global:LASTEXITCODE
