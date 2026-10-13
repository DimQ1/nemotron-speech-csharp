# Captures the RUNNING VoiceType.Uno desktop head (Skia) at phone and desktop sizes
# into app-screenshots/<size>-<state>.png via PrintWindow — the window does not have
# to be on top. States: main screen, menu open, Models page, and the Settings page
# (docs/design/voicetype-uno-settings §5): the section list and every section.
#
# Usage: pwsh docs/design/voicetype-uno-mobile/app-screenshots.ps1 [-Exe <VoiceType.Uno.exe>] [-Sizes 360x800,1280x800] [-States main,settings-storage]
#
# The app runs against a throwaway DEBUG data root (VOICETYPE_DATA_ROOT) with a few placeholder models and recordings, so the screenshots
# never show (or touch) the real %LOCALAPPDATA%\VoiceType. Settings screenshots land in ../voicetype-uno-settings/app-screenshots/.

param(
    [string]$Exe = "$PSScriptRoot\..\..\..\apps\VoiceType.Uno\src\VoiceType.Uno\bin\Debug\net10.0-desktop\VoiceType.Uno.exe",
    [string[]]$Sizes = @('360x800', '800x360', '1280x800'),
    [string[]]$States = @('main', 'menu', 'models', 'settings-hub', 'settings-storage', 'settings-speech', 'settings-audio',
        'settings-translation', 'settings-typing', 'settings-hotkeys', 'settings-general')
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

$outMain = Join-Path $PSScriptRoot 'app-screenshots'
$outSettings = Join-Path $PSScriptRoot '..\voicetype-uno-settings\app-screenshots'
New-Item -ItemType Directory -Force $outMain, $outSettings | Out-Null

$sections = @{
    'storage' = 'Storage'; 'speech' = 'Speech recognition'; 'audio' = 'Audio input'; 'translation' = 'Translation and Gemma'
    'typing' = 'Typing into apps'; 'hotkeys' = 'Keyboard shortcuts'; 'general' = 'General'
}

# Throwaway data root with placeholder models (small files, not real models) and recordings.
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ("voicetype-shots-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$m = "$dataRoot\models"
New-Item -ItemType Directory -Force "$m\asr\parakeet-tdt-0.6b-v3-onnx-int4", "$m\llm", "$dataRoot\chats" | Out-Null
Set-Content "$m\asr\parakeet-tdt-0.6b-v3-onnx-int4\genai_config.json" '{"decoder":{"filename":"decoder.onnx"}}'
foreach ($f in @(@("$m\asr\parakeet-tdt-0.6b-v3-onnx-int4\decoder.onnx", 120MB), @("$m\llm\gemma-4-E2B-it.litertlm", 300MB))) {
    $s = [IO.File]::Create($f[0]); $s.SetLength($f[1]); $s.Close()
}
foreach ($i in 1..3) {
    $d = "$dataRoot\sessions\2026-10-0${i}_120000_abc$i"
    New-Item -ItemType Directory -Force $d | Out-Null
    $s = [IO.File]::Create("$d\audio.opus"); $s.SetLength(5MB); $s.Close()
}
Set-Content "$dataRoot\settings.json" '{"SelectedModel":"parakeet-tdt-0.6b-v3-onnx-int4","TranslationEnabled":true,"AudioSource":"Mix"}'
$env:VOICETYPE_DATA_ROOT = $dataRoot

function Invoke-ByName($hwnd, [string]$name) {
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::NameProperty), $name
    $el = $null
    for ($try = 0; $try -lt 6 -and -not $el; $try++) {
        $el = [Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
        if (-not $el) { Start-Sleep -Milliseconds 700 }
    }
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

try {
    foreach ($size in $Sizes) {
        [int]$w = $size.Split('x')[0]; [int]$h = $size.Split('x')[1]
        foreach ($state in $States) {
            $proc = Start-Process (Resolve-Path $Exe).Path -PassThru
            try {
                $deadline = (Get-Date).AddSeconds(60)
                while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500; $proc.Refresh() }
                $hwnd = $proc.MainWindowHandle
                $btn = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Button)
                while ((Get-Date) -lt $deadline) {
                    try { if ([Windows.Automation.AutomationElement]::FromHandle($hwnd).FindAll([Windows.Automation.TreeScope]::Descendants, $btn).Count -ge 2) { break } } catch { }
                    Start-Sleep -Milliseconds 500
                }

                $scale = [Shot]::GetDpiForWindow($hwnd) / 96.0
                $wr = New-Object Shot+RECT; $cr = New-Object Shot+RECT
                [void][Shot]::GetWindowRect($hwnd, [ref]$wr); [void][Shot]::GetClientRect($hwnd, [ref]$cr)
                [int]$ow = [Math]::Round($w * $scale) + (($wr.Right - $wr.Left) - $cr.Right)
                [int]$oh = [Math]::Round($h * $scale) + (($wr.Bottom - $wr.Top) - $cr.Bottom)
                [void][Shot]::SetWindowPos($hwnd, [IntPtr]::Zero, 40, 40, $ow, $oh, 0x0014)
                Start-Sleep -Seconds 3

                $out = $outMain
                if ($state -ne 'main') { Invoke-ByName $hwnd 'Open menu' }
                if ($state -eq 'models') { Invoke-ByName $hwnd 'Speech models' }
                if ($state -like 'settings-*') {
                    $out = $outSettings
                    Invoke-ByName $hwnd 'Settings'
                    $key = $state.Substring('settings-'.Length)
                    if ($key -ne 'hub') { Invoke-ByName $hwnd $sections[$key] }
                }
                Save-Client $hwnd (Join-Path $out "$size-$state.png")
                Write-Host "saved $size-$state.png"
            }
            finally { if (-not $proc.HasExited) { $proc.Kill() } }
        }
    }
}
finally {
    Remove-Item Env:\VOICETYPE_DATA_ROOT -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
}
