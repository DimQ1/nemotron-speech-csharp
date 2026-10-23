# Renders mockup.html for every scenario with headless Microsoft Edge:
#   screenshots/<name>.png  — the screen at the given viewport
#   layout-check.json       — overlap / safe-area / hit-target report per scenario
#
# Usage:  pwsh docs/design/voicetype-uno-mobile/render.ps1
# Exit code 1 when any scenario reports a layout issue.

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

# Insets in CSS px (= dp/epx): Android status bar 24-48, gesture bar 24-48,
# 3-button nav bar 48 (bottom in portrait, side in landscape); keyboard ~ 40% of height.
$scenarios = @(
    @{ Name = 'phone-idle';                W = 360;  H = 800;  Q = 'top=32&bottom=24' }
    @{ Name = 'phone-recording-translate'; W = 360;  H = 800;  Q = 'top=32&bottom=24&rec=1&tr=1&inject=1' }
    @{ Name = 'phone-source-menu';         W = 360;  H = 800;  Q = 'top=32&bottom=24&rec=1&src=loop&srcmenu=1' }
    @{ Name = 'phone-source-android';      W = 360;  H = 800;  Q = 'top=32&bottom=24&android=1' }
    @{ Name = 'phone-small-source-menu';   W = 320;  H = 568;  Q = 'top=24&bottom=48&rec=1&src=mix&srcmenu=1' }
    @{ Name = 'phone-drawer';              W = 360;  H = 800;  Q = 'top=32&bottom=24&drawer=1&tr=1&inject=1&dl=1' }
    @{ Name = 'phone-banner-download';     W = 360;  H = 800;  Q = 'top=32&bottom=48&banner=1&dl=1' }
    @{ Name = 'phone-manual-keyboard';     W = 360;  H = 800;  Q = 'top=32&bottom=24&manual=1&tr=1&kb=320' }
    @{ Name = 'phone-small-320';           W = 320;  H = 568;  Q = 'top=24&bottom=48&rec=1&muted=1&tr=1&banner=1' }
    @{ Name = 'phone-small-keyboard';      W = 320;  H = 568;  Q = 'top=24&bottom=48&manual=1&tr=1&kb=260' }
    @{ Name = 'phone-small-drawer';        W = 320;  H = 568;  Q = 'top=24&bottom=48&drawer=1&tr=1' }
    @{ Name = 'phone-split-screen';        W = 320;  H = 420;  Q = 'top=24&bottom=48&banner=1&dl=1&tr=1&rec=1' }
    @{ Name = 'phone-split-screen-tr';     W = 320;  H = 420;  Q = 'top=24&bottom=48&tr=1&rec=1&showtr=1' }
    @{ Name = 'phone-landscape';           W = 800;  H = 360;  Q = 'top=24&right=48&rec=1&tr=1&long=1' }
    @{ Name = 'phone-landscape-drawer';    W = 800;  H = 360;  Q = 'top=24&right=48&drawer=1' }
    @{ Name = 'phone-models-speech';       W = 360;  H = 800;  Q = 'top=32&bottom=24&models=1&mtab=speech' }
    @{ Name = 'phone-models-translation';  W = 360;  H = 800;  Q = 'top=32&bottom=24&models=1&mtab=translation' }
    @{ Name = 'phone-models-downloads';    W = 360;  H = 800;  Q = 'top=32&bottom=24&models=1&mtab=downloads' }
    @{ Name = 'phone-small-models';        W = 320;  H = 568;  Q = 'top=24&bottom=48&models=1&mtab=speech' }
    @{ Name = 'phone-landscape-models';    W = 800;  H = 360;  Q = 'top=24&right=48&models=1&mtab=downloads' }
    @{ Name = 'tablet-portrait';           W = 800;  H = 1280; Q = 'top=24&bottom=48&rec=1&tr=1&long=1' }
    @{ Name = 'desktop-recording';         W = 1280; H = 800;  Q = 'rec=1&tr=1&inject=1&long=1' }
    @{ Name = 'desktop-source-mix';        W = 1280; H = 800;  Q = 'rec=1&src=mix&tr=1&inject=1&srcmenu=1' }
    @{ Name = 'desktop-drawer';            W = 1280; H = 800;  Q = 'drawer=1&tr=1&inject=1' }
    @{ Name = 'desktop-models';            W = 1280; H = 800;  Q = 'models=1&mtab=speech' }
    @{ Name = 'desktop-min-window';        W = 480;  H = 560;  Q = 'tr=1&banner=1&dl=1' }
    @{ Name = 'desktop-dark';              W = 1280; H = 800;  Q = 'rec=1&tr=1&long=1&theme=dark' }
)

$edgeProfile = Join-Path ([IO.Path]::GetTempPath()) "voicetype-mockup-edge"
$common = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
            '--force-device-scale-factor=1', "--user-data-dir=$edgeProfile",
            '--virtual-time-budget=1500')

$report = [ordered]@{}
$failed = $false
foreach ($s in $scenarios) {
    # Pin the theme so the output does not depend on the machine's colour scheme.
    if ($s.Q -notmatch 'theme=') { $s.Q += '&theme=light' }
    # frame.html pins the exact viewport; the outer window is larger and the
    # screenshot is cropped back to W x H.
    $win = "--window-size=$([Math]::Max($s.W, 640) + 160),$($s.H + 240)"
    $url = "$frame`?w=$($s.W)&h=$($s.H)&q=$([Uri]::EscapeDataString($s.Q))"
    $png = Join-Path $shots "$($s.Name).png"
    $raw = Join-Path ([IO.Path]::GetTempPath()) "voicetype-mockup-raw.png"
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
    Write-Host ("{0} {1,-28} {2,-10} {3}" -f $status, $s.Name, $json.viewport, ($json.issues -join '; '))
}

$report | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $here 'layout-check.json')
if ($failed) { exit 1 }
