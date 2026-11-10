# Seven Store screenshots of the preview package in one interface language (PrintWindow, window on the left monitor,
# never activated). Usage: pwsh shoot-lang.ps1 -Lang ru -Out <dir>
param([Parameter(Mandatory)][string]$Lang, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\drive.ps1"

$pv = "$env:LOCALAPPDATA\Packages\DimQ1.VoiceType.UnoPreview_310ax279fjzmt\LocalCache\Local\VoiceType"
$target = @{ 'en' = 'de'; 'zh-CN' = 'zh'; 'pt-BR' = 'pt' }[$Lang]; if (-not $target) { $target = $Lang }

# English name -> the names the same control can have in $Lang (one English text can be several keys).
$enMap = @{}
Get-ChildItem "$script:LocRoot\en\*.json" | ForEach-Object {
    (Get-Content $_.FullName -Raw -Encoding utf8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object {
        if (-not $enMap.ContainsKey($_.Value)) { $enMap[$_.Value] = [System.Collections.Generic.List[string]]::new() }
        $enMap[$_.Value].Add($_.Name)
    }
}
function Candidates([string]$english) {
    if (-not $enMap.ContainsKey($english)) { throw "no key for '$english'" }
    $enMap[$english] | ForEach-Object { L $Lang $_ } | Select-Object -Unique
}
# The candidate that is on screen now.
function N([string]$english, [int]$seconds = 20) {
    $c = @(Candidates $english)
    if ($c.Count -eq 1) { return $c[0] }
    $deadline = (Get-Date).AddSeconds($seconds)
    do {
        $names = Get-Names $script:h 500 | ForEach-Object { $_ -replace ' \[[A-Za-z]+\]$', '' }
        foreach ($x in $c) { if ($names -contains $x) { return $x } }
        Start-Sleep 1
    } while ((Get-Date) -lt $deadline)
    throw "none of '$($c -join "', '")' on screen"
}
function Click([string]$english, [int]$wait = 1800) { Invoke-ByName $script:h (N $english) $wait }
function Home {
    $menu = @(Candidates 'Open menu')
    for ($i = 0; $i -lt 5; $i++) {
        $names = Get-Names $script:h 300 | ForEach-Object { $_ -replace ' \[[A-Za-z]+\]$', '' }
        if ($names | Where-Object { $menu -contains $_ }) { return }
        Click 'Back' 2000
    }
}
function ClickMatch([string]$regex, [int]$wait = 2500) {
    $name = $null
    for ($t = 0; $t -lt 10 -and -not $name; $t++) {
        $name = (Get-Names $script:h 400 | Where-Object { $_ -match $regex } | Select-Object -First 1) -replace ' \[[A-Za-z]+\]$', ''
        if (-not $name) { Start-Sleep 1 }
    }
    if (-not $name) { throw "nothing matches /$regex/" }
    Invoke-ByName $script:h $name $wait
}
function Shot([string]$file) { Start-Sleep -Milliseconds 1500; Save-Client $script:h (Join-Path $Out $file) }

# Settings of this run (the rest of settings.json stays as the app wrote it).
$settingsFile = "$pv\settings.json"
$s = Get-Content $settingsFile -Raw -Encoding utf8 | ConvertFrom-Json
$s.UiLanguage = $Lang; $s.TranslationTargetLanguage = $target; $s.TranslationEnabled = $true; $s.FirstRunCompleted = $true
$s | ConvertTo-Json -Depth 20 | Set-Content $settingsFile -Encoding utf8

New-Item -ItemType Directory -Force $Out | Out-Null
Get-Process VoiceType.Uno -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like '*UnoPreview*' } catch { $false } } | Stop-Process -Force
Start-Process "shell:AppsFolder\DimQ1.VoiceType.UnoPreview_310ax279fjzmt!App"
$script:h = Get-VoiceTypeWindow
Set-ClientSize $script:h 1920 1080
Wait-Name $script:h ([regex]::Escape((N 'Start recording'))) 90 | Out-Null
Start-Sleep 4

# 02 · main screen: manual input + live translation
Click 'Open menu'
Click 'Manual input'
Click 'Close menu'
$before = @(Get-Names $script:h 400)
Set-ValueByName $script:h (N 'Transcript') 'Hi Anna, thanks for the quick reply. I went through the draft this morning and it looks great. Could we move the review to Friday at ten? I will send the updated slides before lunch.'
$deadline = (Get-Date).AddSeconds(240)
do {
    Start-Sleep 3
    $new = Get-Names $script:h 400 | Where-Object { $_ -like '*[[]Text[]]' -and $_.Length -gt 70 -and $before -notcontains $_ }
} while (-not $new -and (Get-Date) -lt $deadline)
Start-Sleep 6   # the translation streams in: let it finish
Shot '02-live-translation.png'
Click 'Open menu'
Click 'Manual input'

# 01 · History with speakers
Click 'History' 3000
ClickMatch '^Product sync: release 2\.0 · .*\[Button\]'
Shot '01-history-speakers.png'

# 04 · the Russian recording translated to English
ClickMatch '^Планёрка: задачи на неделю · .*\[Button\]'
Click 'Translate' 2500
Shot '04-translate-recording.png'
Home

# 03 · Ask Gemma across two recordings
Click 'Open menu'
Click 'Chats' 3000
ClickMatch '^Release 2\.0 and customer feedback.*\[Button\]' 3500
Shot '03-ask-gemma.png'
Home

# 05, 06 · Settings
Click 'Open menu'
Click 'Settings' 3000
Click 'Typing into apps' 2500
Shot '05-typing-into-apps.png'
Click 'Keyboard shortcuts' 2500
Shot '06-hotkeys.png'

Get-Process VoiceType.Uno -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like '*UnoPreview*' } catch { $false } } | Stop-Process -Force
"done $Lang"
