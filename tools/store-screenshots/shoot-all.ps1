param([string[]]$Langs = @('en','ru','de','fr','es','zh-CN','ja','ko','pt-BR','it','ar','hi','tr','uk','pl','nl'),
      [string]$Root = (Join-Path $PSScriptRoot '..\..\docs\store\imports\v2.0.0\screenshots'))
$log = Join-Path ([IO.Path]::GetTempPath()) 'voicetype-shoot-all.log'
foreach ($lang in $Langs) {
    $out = Join-Path $Root $lang
    $ok = $false
    for ($try = 1; $try -le 2 -and -not $ok; $try++) {
        try {
            & "$PSScriptRoot\shoot-lang.ps1" -Lang $lang -Out $out | Out-Null
            $n = (Get-ChildItem $out -Filter *.png).Count
            $ok = $n -ge 6
            Add-Content $log "$(Get-Date -Format HH:mm:ss) $lang try $try -> $n files"
        } catch {
            Add-Content $log "$(Get-Date -Format HH:mm:ss) $lang try $try FAILED: $($_.Exception.Message)"
            Get-Process VoiceType.Uno -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like '*UnoPreview*' } catch { $false } } | Stop-Process -Force
        }
    }
}
Add-Content $log "$(Get-Date -Format HH:mm:ss) ALL DONE"
