# Extra helpers over tools/store-screenshots/shot.ps1 (no focus changes, no screen grabs).
. "$PSScriptRoot\shot.ps1"

function Set-ValueByName($hwnd, [string]$name, [string]$value) {
    $el = Find-ByName $hwnd $name
    if (-not $el) { throw "'$name' not found" }
    $p = $null
    if ($el.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.SetValue($value); return }
    throw "'$name' has no ValuePattern"
}

function Wait-Name($hwnd, [string]$pattern, [int]$seconds = 120) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $n = Get-Names $hwnd 400
        $hit = $n | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if ($hit) { return $hit }
        Start-Sleep 2
    }
    throw "timeout waiting for /$pattern/"
}

# Localized text of a key (the app's own string tables), so the same script drives every language.
$script:LocRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\apps\VoiceType.Uno\src\VoiceType.Uno.Core\Localization\Strings')).Path
$script:LocCache = @{}
function L([string]$lang, [string]$key) {
    if (-not $script:LocCache.ContainsKey($lang)) {
        $t = @{}
        foreach ($f in Get-ChildItem "$script:LocRoot\$lang\*.json") {
            (Get-Content $f.FullName -Raw -Encoding utf8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $t[$_.Name] = $_.Value }
        }
        $script:LocCache[$lang] = $t
    }
    $v = $script:LocCache[$lang][$key]
    if (-not $v) { $v = $script:LocCache['en'][$key] }
    if (-not $v) { throw "no key $key" }
    return $v
}
$null = L 'en' 'Main.Menu.History'
