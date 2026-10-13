# Renders mockup.html for every scenario with headless Microsoft Edge:
#   screenshots/<name>.png  — the screen at the given viewport
#   layout-check.json       — overlap / safe-area / hit-target / clipped-path report per scenario
#
# Same harness as ../voicetype-uno-history/render.ps1, Settings scenarios.
# Usage:  pwsh docs/design/voicetype-uno-settings/render.ps1 [-Only <name-substring>]
# Exit code 1 when any scenario reports a layout issue.
param([string]$Only = '')

$ErrorActionPreference = 'Stop'

$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }

$here = $PSScriptRoot
$frame = ([Uri](Join-Path $here 'frame.html')).AbsoluteUri
Add-Type -AssemblyName System.Drawing
$shots = Join-Path $here 'screenshots'
New-Item -ItemType Directory -Force $shots | Out-Null

# Insets in CSS px (= dp/epx): status bar, gesture bar / 3-button nav bar, keyboard.
$P  = 'os=android&top=32&bottom=24'   # Android phone 360x800, gesture navigation
$SM = 'os=android&top=24&bottom=48'   # small Android phone 320x568, 3-button navigation
$LS = 'os=android&top=24&right=48'    # Android landscape 800x360, nav bar on the right
$scenarios = @(
    # ── Android phone 360x800 (folders are read-only: the app's private storage) ──
    @{ Name = 'phone-hub';                    W = 360;  H = 800;  Q = "$P&page=hub" }
    @{ Name = 'phone-storage';                W = 360;  H = 800;  Q = "$P&page=storage" }
    @{ Name = 'phone-speech';                 W = 360;  H = 800;  Q = "$P&page=speech" }
    @{ Name = 'phone-speech-advanced';        W = 360;  H = 800;  Q = "$P&page=speech&adv=1" }
    @{ Name = 'phone-audio';                  W = 360;  H = 800;  Q = "$P&page=audio" }
    @{ Name = 'phone-translation';            W = 360;  H = 800;  Q = "$P&page=translation" }
    @{ Name = 'phone-translation-keyboard';   W = 360;  H = 800;  Q = "$P&page=translation&kb=320" }
    @{ Name = 'phone-general';                W = 360;  H = 800;  Q = "$P&page=general" }
    # ── small Android phone 320x568 ──
    @{ Name = 'small-hub';                    W = 320;  H = 568;  Q = "$SM&page=hub" }
    @{ Name = 'small-storage';                W = 320;  H = 568;  Q = "$SM&page=storage" }
    # ── Android landscape 800x360 ──
    @{ Name = 'landscape-hub';                W = 800;  H = 360;  Q = "$LS&page=hub" }
    @{ Name = 'landscape-storage';            W = 800;  H = 360;  Q = "$LS&page=storage" }
    # ── tablet 800x1280 (Android, single pane below 840 px) ──
    @{ Name = 'tablet-storage';               W = 800;  H = 1280; Q = 'os=android&top=24&bottom=48&page=storage' }
    # ── Windows / Linux desktop 1280x800 ──
    @{ Name = 'desktop-storage';              W = 1280; H = 800;  Q = 'page=storage' }
    @{ Name = 'desktop-models-move';          W = 1280; H = 800;  Q = 'page=storage&sheet=move&kind=models' }
    @{ Name = 'desktop-models-move-other-drive'; W = 1280; H = 800; Q = 'page=storage&sheet=move&kind=models&drive=other' }
    @{ Name = 'desktop-models-pending';       W = 1280; H = 800;  Q = 'page=storage&state=pending&kind=models' }
    @{ Name = 'desktop-models-incomplete';    W = 1280; H = 800;  Q = 'page=storage&state=incomplete&kind=models' }
    @{ Name = 'desktop-recordings-move';      W = 1280; H = 800;  Q = 'page=storage&sheet=move&kind=recordings' }
    @{ Name = 'desktop-data-move';            W = 1280; H = 800;  Q = 'page=storage&sheet=move&kind=data' }
    @{ Name = 'desktop-data-pending';         W = 1280; H = 800;  Q = 'page=storage&state=pending&kind=data' }
    @{ Name = 'desktop-speech';               W = 1280; H = 800;  Q = 'page=speech&adv=1' }
    @{ Name = 'desktop-audio';                W = 1280; H = 800;  Q = 'page=audio' }
    @{ Name = 'desktop-translation';          W = 1280; H = 800;  Q = 'page=translation' }
    @{ Name = 'desktop-translation-server';   W = 1280; H = 800;  Q = 'page=translation&srv=1' }
    @{ Name = 'desktop-typing';               W = 1280; H = 800;  Q = 'page=typing' }
    @{ Name = 'desktop-hotkeys';              W = 1280; H = 800;  Q = 'page=hotkeys&rec=1' }
    @{ Name = 'desktop-hotkeys-conflict';     W = 1280; H = 800;  Q = 'page=hotkeys&conflict=1' }
    @{ Name = 'desktop-general';              W = 1280; H = 800;  Q = 'page=general' }
    @{ Name = 'desktop-dark-storage';         W = 1280; H = 800;  Q = 'page=storage&theme=dark' }
    @{ Name = 'desktop-dark-move';            W = 1280; H = 800;  Q = 'page=storage&sheet=move&kind=models&theme=dark' }
    # ── small desktop window 480x560 (single pane, same as a phone) ──
    @{ Name = 'desktop-min-hub';              W = 480;  H = 560;  Q = 'page=hub' }
    @{ Name = 'desktop-min-storage';          W = 480;  H = 560;  Q = 'page=storage&state=pending&kind=models' }
    @{ Name = 'desktop-min-move';             W = 480;  H = 560;  Q = 'page=storage&sheet=move&kind=models' }
    @{ Name = 'desktop-min-move-nested';      W = 480;  H = 560;  Q = 'page=storage&sheet=move&kind=models&drive=nested' }
)
if ($Only) { $scenarios = $scenarios | Where-Object { $_.Name -like "*$Only*" } }

$edgeProfile = Join-Path ([IO.Path]::GetTempPath()) "voicetype-settings-mockup-edge"
$common = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            '--force-device-scale-factor=1', "--user-data-dir=$edgeProfile",
            '--virtual-time-budget=1500')

$reportPath = Join-Path $here 'layout-check.json'
$report = [ordered]@{}
$failed = $false
foreach ($s in $scenarios) {
    if ($s.Q -notmatch 'theme=') { $s.Q += '&theme=light' }
    $win = "--window-size=$([Math]::Max($s.W, 640) + 160),$($s.H + 240)"
    $url = "$frame`?w=$($s.W)&h=$($s.H)&q=$([Uri]::EscapeDataString($s.Q))"
    $png = Join-Path $shots "$($s.Name).png"
    $raw = Join-Path ([IO.Path]::GetTempPath()) "voicetype-settings-mockup-raw.png"
    & $edge @common $win "--screenshot=$raw" $url 2>$null | Out-Null
    $src = [Drawing.Bitmap]::FromFile($raw)
    try { $src.Clone([Drawing.Rectangle]::new(0, 0, $s.W, $s.H), $src.PixelFormat).Save($png, [Drawing.Imaging.ImageFormat]::Png) }
    finally { $src.Dispose() }

    $checkUrl = "$frame`?w=$($s.W)&h=$($s.H)&q=$([Uri]::EscapeDataString($s.Q + '&check=1'))"
    $dom = & $edge @common $win '--dump-dom' $checkUrl 2>$null | Out-String
    $m = [regex]::Match($dom, '<pre id="check">(.*?)</pre>', 'Singleline')
    if (-not $m.Success) { throw "No layout report for $($s.Name)" }
    $json = [Net.WebUtility]::HtmlDecode($m.Groups[1].Value) | ConvertFrom-Json
    $report[$s.Name] = $json

    $status = if ($json.ok) { 'OK  ' } else { $failed = $true; 'FAIL' }
    Write-Host ("{0} {1,-30} {2,-9} {3,-6} {4}" -f $status, $s.Name, $json.viewport, $json.layer, ($json.issues -join '; '))
}

if (-not $Only) { $report | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 $reportPath }
Write-Host ("{0}/{1} scenarios OK" -f @($report.Values | Where-Object { $_.ok }).Count, $report.Count)
if ($failed) { exit 1 }
