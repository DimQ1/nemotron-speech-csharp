# Screenshot the emulator to a PNG. Usage: snap.ps1 [-Path shot.png]
param([string]$Path = 'shot.png', [int]$Port = 5554)
$sdk = if ($env:VT_ANDROID_SDK) { $env:VT_ANDROID_SDK } else { 'E:\Android\sdk' }
$adb = Join-Path $sdk 'platform-tools\adb.exe'
$tmp = [IO.Path]::GetTempFileName()
cmd /c "`"$adb`" -s emulator-$Port exec-out screencap -p > `"$tmp`""
Move-Item $tmp ([IO.Path]::GetFullPath($Path, (Get-Location).Path)) -Force

