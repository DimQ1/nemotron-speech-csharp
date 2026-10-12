# Renders mockup.html for every scenario with headless Microsoft Edge:
#   screenshots/<name>.png  — the screen at the given viewport
#   layout-check.json       — overlap / safe-area / hit-target report per scenario
#
# Adapted from ../voicetype-uno-mobile/render.ps1 (same harness, History scenarios).
# Usage:  pwsh docs/design/voicetype-uno-history/render.ps1 [-Only <name-substring>]
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

# Insets in CSS px (= dp/epx): Android status bar 24-48, gesture bar 24-48,
# 3-button nav bar 48 (bottom in portrait, side in landscape); keyboard ~ 40% of height.
$P  = 'top=32&bottom=24'      # phone 360x800, gesture navigation
$SM = 'top=24&bottom=48'      # small phone 320x568, 3-button navigation
$LS = 'top=24&right=48'       # phone landscape 800x360, nav bar on the right
$scenarios = @(
    # ── phone 360x800 ──
    @{ Name = 'phone-main-saved';            W = 360;  H = 800;  Q = "$P&snack=1" }
    @{ Name = 'phone-drawer-history';        W = 360;  H = 800;  Q = "$P&drawer=1" }
    @{ Name = 'phone-history';               W = 360;  H = 800;  Q = "$P&screen=history" }
    @{ Name = 'phone-history-search';        W = 360;  H = 800;  Q = "$P&screen=history&q=budget&kb=320" }
    @{ Name = 'phone-history-select';        W = 360;  H = 800;  Q = "$P&screen=history&sel=1" }
    @{ Name = 'phone-history-filter';        W = 360;  H = 800;  Q = "$P&screen=history&sheet=filter" }
    @{ Name = 'phone-batch-sheet';           W = 360;  H = 800;  Q = "$P&screen=history&sel=1&sheet=batch" }
    @{ Name = 'phone-delete-confirm';        W = 360;  H = 800;  Q = "$P&screen=history&sel=1&sheet=delete" }
    @{ Name = 'phone-transcript-playing';    W = 360;  H = 800;  Q = "$P&screen=detail&tab=transcript&playing=1" }
    @{ Name = 'phone-transcript-noaudio';    W = 360;  H = 800;  Q = "$P&screen=detail&tab=transcript&noaudio=1" }
    @{ Name = 'phone-transcript-edit';       W = 360;  H = 800;  Q = "$P&screen=detail&tab=transcript&edit=1&kb=320" }
    @{ Name = 'phone-rename';                W = 360;  H = 800;  Q = "$P&screen=detail&tab=transcript&sheet=rename&kb=320" }
    @{ Name = 'phone-export-sheet';          W = 360;  H = 800;  Q = "$P&screen=detail&tab=transcript&sheet=export" }
    @{ Name = 'phone-recording-chats';       W = 360;  H = 800;  Q = "$P&screen=detail&tab=chats" }
    @{ Name = 'phone-addto-sheet';           W = 360;  H = 800;  Q = "$P&screen=detail&tab=chats&sheet=addto" }
    @{ Name = 'phone-chats';                 W = 360;  H = 800;  Q = "$P&screen=chats" }
    @{ Name = 'phone-chat-empty';            W = 360;  H = 800;  Q = "$P&screen=chat&chat=empty" }
    @{ Name = 'phone-chat-streaming';        W = 360;  H = 800;  Q = "$P&screen=chat&chat=streaming" }
    @{ Name = 'phone-chat-keyboard';         W = 360;  H = 800;  Q = "$P&screen=chat&kb=320" }
    @{ Name = 'phone-chat-add-source';       W = 360;  H = 800;  Q = "$P&screen=chat&sheet=addsrc" }
    @{ Name = 'phone-chat-sources';          W = 360;  H = 800;  Q = "$P&screen=chat&sheet=sources" }
    @{ Name = 'phone-chat-export';           W = 360;  H = 800;  Q = "$P&screen=chat&sheet=export&exp=chat" }
    @{ Name = 'phone-translation';           W = 360;  H = 800;  Q = "$P&screen=detail&tab=translation&playing=1&orig=1" }
    @{ Name = 'phone-translation-progress';  W = 360;  H = 800;  Q = "$P&screen=detail&tab=translation&trp=1" }
    @{ Name = 'phone-versions-running';      W = 360;  H = 800;  Q = "$P&screen=detail&tab=versions&rr=1" }
    @{ Name = 'phone-versions-diff';         W = 360;  H = 800;  Q = "$P&screen=detail&tab=versions&diff=1&playing=1" }
    @{ Name = 'phone-rerecognize-sheet';     W = 360;  H = 800;  Q = "$P&screen=detail&tab=versions&sheet=rerun" }
    @{ Name = 'phone-queue';                 W = 360;  H = 800;  Q = "$P&screen=queue" }
    @{ Name = 'phone-settings';              W = 360;  H = 800;  Q = "$P&screen=settings" }
    @{ Name = 'phone-live-speakers';          W = 360; H = 800; Q = "$P&live=1" }
    @{ Name = 'phone-live-fallback';          W = 360; H = 800; Q = "$P&live=2" }
    @{ Name = 'phone-transcript-speakers';    W = 360; H = 800; Q = "$P&screen=detail&tab=transcript&spk=1&playing=1" }
    @{ Name = 'phone-speakers-filter';        W = 360; H = 800; Q = "$P&screen=detail&tab=transcript&spk=1&fspk=1&playing=1" }
    @{ Name = 'phone-speakers-sheet';         W = 360; H = 800; Q = "$P&screen=detail&tab=transcript&spk=1&sheet=speakers" }
    @{ Name = 'phone-speakers-merge';         W = 360; H = 800; Q = "$P&screen=detail&tab=transcript&spk=1&sheet=speakers&merge=1" }
    @{ Name = 'phone-versions-speakers';      W = 360; H = 800; Q = "$P&screen=detail&tab=versions&spk=1&rp=1" }
    @{ Name = 'phone-reprocess-sheet';        W = 360; H = 800; Q = "$P&screen=detail&tab=versions&spk=1&sheet=reprocess" }
    @{ Name = 'phone-recording-insights';     W = 360; H = 800; Q = "$P&screen=detail&tab=chats&spk=1&ins=1" }
    @{ Name = 'phone-queue-chain';            W = 360; H = 800; Q = "$P&screen=queue&chain=1" }
    @{ Name = 'phone-models-speakers';        W = 360; H = 800; Q = "$P&screen=models" }
    # ── small phone 320x568 ──
    @{ Name = 'small-main-saved';            W = 320;  H = 568;  Q = "$SM&snack=1" }
    @{ Name = 'small-history';               W = 320;  H = 568;  Q = "$SM&screen=history" }
    @{ Name = 'small-history-select';        W = 320;  H = 568;  Q = "$SM&screen=history&sel=1" }
    @{ Name = 'small-batch-sheet';           W = 320;  H = 568;  Q = "$SM&screen=history&sel=1&sheet=batch" }
    @{ Name = 'small-delete-confirm';        W = 320;  H = 568;  Q = "$SM&screen=history&sel=1&sheet=delete" }
    @{ Name = 'small-transcript';            W = 320;  H = 568;  Q = "$SM&screen=detail&tab=transcript&playing=1" }
    @{ Name = 'small-transcript-edit';       W = 320;  H = 568;  Q = "$SM&screen=detail&tab=transcript&edit=1&kb=260" }
    @{ Name = 'small-rename';                W = 320;  H = 568;  Q = "$SM&screen=detail&tab=transcript&sheet=rename&kb=260" }
    @{ Name = 'small-export-sheet';          W = 320;  H = 568;  Q = "$SM&screen=detail&tab=transcript&sheet=export" }
    @{ Name = 'small-recording-chats';       W = 320;  H = 568;  Q = "$SM&screen=detail&tab=chats" }
    @{ Name = 'small-chats';                 W = 320;  H = 568;  Q = "$SM&screen=chats" }
    @{ Name = 'small-chat';                  W = 320;  H = 568;  Q = "$SM&screen=chat&chat=streaming" }
    @{ Name = 'small-chat-keyboard';         W = 320;  H = 568;  Q = "$SM&screen=chat&kb=260" }
    @{ Name = 'small-chat-add-source';       W = 320;  H = 568;  Q = "$SM&screen=chat&sheet=addsrc" }
    @{ Name = 'small-chat-sources';          W = 320;  H = 568;  Q = "$SM&screen=chat&sheet=sources" }
    @{ Name = 'small-versions-diff';         W = 320;  H = 568;  Q = "$SM&screen=detail&tab=versions&diff=1" }
    @{ Name = 'small-rerecognize-sheet';     W = 320;  H = 568;  Q = "$SM&screen=detail&tab=versions&sheet=rerun" }
    @{ Name = 'small-queue';                 W = 320;  H = 568;  Q = "$SM&screen=queue" }
    @{ Name = 'small-settings';              W = 320;  H = 568;  Q = "$SM&screen=settings" }
    @{ Name = 'small-live-speakers';          W = 320; H = 568; Q = "$SM&live=1" }
    @{ Name = 'small-transcript-speakers';    W = 320; H = 568; Q = "$SM&screen=detail&tab=transcript&spk=1&playing=1" }
    @{ Name = 'small-speakers-filter';        W = 320; H = 568; Q = "$SM&screen=detail&tab=transcript&spk=1&fspk=1&playing=1" }
    @{ Name = 'small-speakers-sheet';         W = 320; H = 568; Q = "$SM&screen=detail&tab=transcript&spk=1&sheet=speakers" }
    @{ Name = 'small-speakers-merge';         W = 320; H = 568; Q = "$SM&screen=detail&tab=transcript&spk=1&sheet=speakers&merge=1" }
    @{ Name = 'small-versions-speakers';      W = 320; H = 568; Q = "$SM&screen=detail&tab=versions&spk=1&rp=1" }
    @{ Name = 'small-reprocess-sheet';        W = 320; H = 568; Q = "$SM&screen=detail&tab=versions&spk=1&sheet=reprocess" }
    @{ Name = 'small-recording-insights';     W = 320; H = 568; Q = "$SM&screen=detail&tab=chats&spk=1&ins=1" }
    @{ Name = 'small-queue-chain';            W = 320; H = 568; Q = "$SM&screen=queue&chain=1" }
    @{ Name = 'small-models-speakers';        W = 320; H = 568; Q = "$SM&screen=models" }
    # ── phone landscape 800x360 ──
    @{ Name = 'landscape-main-saved';        W = 800;  H = 360;  Q = "$LS&snack=1" }
    @{ Name = 'landscape-history';           W = 800;  H = 360;  Q = "$LS&screen=history" }
    @{ Name = 'landscape-batch-sheet';       W = 800;  H = 360;  Q = "$LS&screen=history&sel=1&sheet=batch" }
    @{ Name = 'landscape-delete-confirm';    W = 800;  H = 360;  Q = "$LS&screen=history&sel=1&sheet=delete" }
    @{ Name = 'landscape-transcript';        W = 800;  H = 360;  Q = "$LS&screen=detail&tab=transcript&playing=1" }
    @{ Name = 'landscape-transcript-edit';   W = 800;  H = 360;  Q = "$LS&screen=detail&tab=transcript&edit=1" }
    @{ Name = 'landscape-rename';            W = 800;  H = 360;  Q = "$LS&screen=detail&tab=transcript&sheet=rename" }
    @{ Name = 'landscape-export-sheet';      W = 800;  H = 360;  Q = "$LS&screen=detail&tab=transcript&sheet=export" }
    @{ Name = 'landscape-recording-chats';   W = 800;  H = 360;  Q = "$LS&screen=detail&tab=chats" }
    @{ Name = 'landscape-chats';             W = 800;  H = 360;  Q = "$LS&screen=chats" }
    @{ Name = 'landscape-chat';              W = 800;  H = 360;  Q = "$LS&screen=chat&chat=thread" }
    @{ Name = 'landscape-chat-add-source';   W = 800;  H = 360;  Q = "$LS&screen=chat&sheet=addsrc" }
    @{ Name = 'landscape-chat-sources';      W = 800;  H = 360;  Q = "$LS&screen=chat&sheet=sources" }
    @{ Name = 'landscape-versions';          W = 800;  H = 360;  Q = "$LS&screen=detail&tab=versions&rr=1" }
    @{ Name = 'landscape-rerecognize-sheet'; W = 800;  H = 360;  Q = "$LS&screen=detail&tab=versions&sheet=rerun" }
    @{ Name = 'landscape-queue';             W = 800;  H = 360;  Q = "$LS&screen=queue" }
    @{ Name = 'landscape-settings';          W = 800;  H = 360;  Q = "$LS&screen=settings" }
    @{ Name = 'landscape-live-speakers';      W = 800; H = 360; Q = "$LS&live=1" }
    @{ Name = 'landscape-transcript-speakers'; W = 800; H = 360; Q = "$LS&screen=detail&tab=transcript&spk=1&playing=1" }
    @{ Name = 'landscape-speakers-sheet';     W = 800; H = 360; Q = "$LS&screen=detail&tab=transcript&spk=1&sheet=speakers" }
    @{ Name = 'landscape-versions-speakers';  W = 800; H = 360; Q = "$LS&screen=detail&tab=versions&spk=1&rp=1" }
    @{ Name = 'landscape-reprocess-sheet';    W = 800; H = 360; Q = "$LS&screen=detail&tab=versions&spk=1&sheet=reprocess" }
    @{ Name = 'landscape-recording-insights'; W = 800; H = 360; Q = "$LS&screen=detail&tab=chats&spk=1&ins=1" }
    @{ Name = 'landscape-queue-chain';        W = 800; H = 360; Q = "$LS&screen=queue&chain=1" }
    @{ Name = 'landscape-models-speakers';    W = 800; H = 360; Q = "$LS&screen=models" }
    # ── desktop 1280x800 ──
    @{ Name = 'desktop-main-saved';          W = 1280; H = 800;  Q = 'snack=1' }
    @{ Name = 'desktop-history-empty';       W = 1280; H = 800;  Q = 'screen=history' }
    @{ Name = 'desktop-batch-dialog';        W = 1280; H = 800;  Q = 'screen=history&sel=1&sheet=batch' }
    @{ Name = 'desktop-delete-confirm';      W = 1280; H = 800;  Q = 'screen=history&sel=1&sheet=delete' }
    @{ Name = 'desktop-transcript';          W = 1280; H = 800;  Q = 'screen=detail&tab=transcript&playing=1' }
    @{ Name = 'desktop-transcript-edit';     W = 1280; H = 800;  Q = 'screen=detail&tab=transcript&edit=1' }
    @{ Name = 'desktop-rename';              W = 1280; H = 800;  Q = 'screen=detail&tab=transcript&sheet=rename' }
    @{ Name = 'desktop-export-dialog';       W = 1280; H = 800;  Q = 'screen=detail&tab=transcript&sheet=export' }
    @{ Name = 'desktop-recording-chats';     W = 1280; H = 800;  Q = 'screen=detail&tab=chats' }
    @{ Name = 'desktop-chats-empty';         W = 1280; H = 800;  Q = 'screen=chats' }
    @{ Name = 'desktop-chat';                W = 1280; H = 800;  Q = 'screen=chat&chat=streaming' }
    @{ Name = 'desktop-chat-add-source';     W = 1280; H = 800;  Q = 'screen=chat&sheet=addsrc' }
    @{ Name = 'desktop-chat-export';         W = 1280; H = 800;  Q = 'screen=chat&sheet=export&exp=chat' }
    @{ Name = 'desktop-versions-diff';       W = 1280; H = 800;  Q = 'screen=detail&tab=versions&diff=1&playing=1' }
    @{ Name = 'desktop-rerecognize-dialog';  W = 1280; H = 800;  Q = 'screen=detail&tab=versions&sheet=rerun' }
    @{ Name = 'desktop-dark-translation';    W = 1280; H = 800;  Q = 'screen=detail&tab=translation&playing=1&orig=1&theme=dark' }
    @{ Name = 'desktop-queue';               W = 1280; H = 800;  Q = 'screen=queue' }
    @{ Name = 'desktop-settings';            W = 1280; H = 800;  Q = 'screen=settings' }
    @{ Name = 'desktop-dark-chat';           W = 1280; H = 800;  Q = 'screen=chat&chat=thread&theme=dark' }
    @{ Name = 'desktop-live-speakers';        W = 1280; H = 800; Q = 'live=1' }
    @{ Name = 'desktop-transcript-speakers';  W = 1280; H = 800; Q = 'screen=detail&tab=transcript&spk=1&playing=1' }
    @{ Name = 'desktop-speakers-filter';      W = 1280; H = 800; Q = 'screen=detail&tab=transcript&spk=1&fspk=1&playing=1' }
    @{ Name = 'desktop-speakers-sheet';       W = 1280; H = 800; Q = 'screen=detail&tab=transcript&spk=1&sheet=speakers' }
    @{ Name = 'desktop-speakers-merge';       W = 1280; H = 800; Q = 'screen=detail&tab=transcript&spk=1&sheet=speakers&merge=1' }
    @{ Name = 'desktop-reprocess-dialog';     W = 1280; H = 800; Q = 'screen=detail&tab=versions&spk=1&sheet=reprocess' }
    @{ Name = 'desktop-versions-speakers';    W = 1280; H = 800; Q = 'screen=detail&tab=versions&spk=1&rp=1' }
    @{ Name = 'desktop-recording-insights';   W = 1280; H = 800; Q = 'screen=detail&tab=chats&spk=1&ins=1' }
    @{ Name = 'desktop-queue-chain';          W = 1280; H = 800; Q = 'screen=queue&chain=1' }
    @{ Name = 'desktop-models-speakers';      W = 1280; H = 800; Q = 'screen=models' }
    @{ Name = 'desktop-dark-speakers';        W = 1280; H = 800; Q = 'screen=detail&tab=transcript&spk=1&playing=1&theme=dark' }
    @{ Name = 'desktop-min-window';          W = 480;  H = 560;  Q = 'screen=detail&tab=transcript&playing=1' }
)
if ($Only) { $scenarios = $scenarios | Where-Object { $_.Name -like "*$Only*" } }

$edgeProfile = Join-Path ([IO.Path]::GetTempPath()) "voicetype-history-mockup-edge"
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
    $raw = Join-Path ([IO.Path]::GetTempPath()) "voicetype-history-mockup-raw.png"
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
