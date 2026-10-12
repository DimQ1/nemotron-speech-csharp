<#
.SYNOPSIS  Start the voicetype-api35 AVD, wait for boot, apply dev settings.
.PARAMETER Headless  Run without a window (-no-window).
.PARAMETER ColdBoot  Ignore quick-boot snapshots.
#>
param([switch]$Headless, [switch]$ColdBoot, [string]$Avd = 'voicetype-api35', [int]$Port = 5554)
$ErrorActionPreference = 'Stop'
$sdk = if ($env:VT_ANDROID_SDK) { $env:VT_ANDROID_SDK } else { 'E:\Android\sdk' }
$env:ANDROID_SDK_ROOT = $sdk
$env:ANDROID_AVD_HOME = if ($env:VT_ANDROID_AVD_HOME) { $env:VT_ANDROID_AVD_HOME } else { 'E:\Android\avd' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$serial = "emulator-$Port"

$running = (& $adb devices) -match "^$serial\s+device"
if (-not $running) {
    $a = @('-avd', $Avd, '-port', $Port, '-allow-host-audio', '-no-boot-anim', '-netdelay', 'none', '-netspeed', 'full')
    if ($Headless) { $a += '-no-window' }
    if ($ColdBoot) { $a += '-no-snapshot-load' }
    Start-Process -FilePath (Join-Path $sdk 'emulator\emulator.exe') -ArgumentList $a -WindowStyle Hidden
}
$sw = [Diagnostics.Stopwatch]::StartNew()
& $adb -s $serial wait-for-device
while ((& $adb -s $serial shell getprop sys.boot_completed 2>$null) -notmatch '1') {
    if ($sw.Elapsed.TotalSeconds -gt 300) { throw 'emulator did not boot in 300 s' }
    Start-Sleep 2
}
& $adb -s $serial shell settings put global stay_on_while_plugged_in 7
& $adb -s $serial shell settings put system screen_off_timeout 2147483647
foreach ($s in 'window_animation_scale', 'transition_animation_scale', 'animator_duration_scale') {
    & $adb -s $serial shell settings put global $s 0
}
& $adb -s $serial shell input keyevent 82   # dismiss keyguard
Write-Host "$serial ready after $([int]$sw.Elapsed.TotalSeconds) s"
