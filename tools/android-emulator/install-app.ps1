<#
.SYNOPSIS  Install the latest Release APK on the emulator only and grant RECORD_AUDIO.
.PARAMETER Apk     Explicit APK path (default: newest *-Signed.apk under bin/Release).
.PARAMETER Launch  Start the app afterwards.
#>
param([string]$Apk, [switch]$Launch, [int]$Port = 5554)
$ErrorActionPreference = 'Stop'
$sdk = if ($env:VT_ANDROID_SDK) { $env:VT_ANDROID_SDK } else { 'E:\Android\sdk' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$serial = "emulator-$Port"
$pkg = 'com.companyname.voicetype_uno'
if (-not $Apk) {
    $root = Join-Path $PSScriptRoot '..\..\apps\VoiceType.Uno\src\VoiceType.Uno\bin\Release'
    $Apk = (Get-ChildItem $root -Recurse -Filter '*-Signed.apk' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Apk -or -not (Test-Path $Apk)) { throw 'No APK found; build net10.0-android Release first.' }
Write-Host "Installing $Apk on $serial"
& $adb -s $serial install -r $Apk
& $adb -s $serial shell pm grant $pkg android.permission.RECORD_AUDIO
& $adb -s $serial shell pm grant $pkg android.permission.POST_NOTIFICATIONS 2>$null
if ($Launch) { & $adb -s $serial shell monkey -p $pkg -c android.intent.category.LAUNCHER 1 2>&1 | Out-Null }

