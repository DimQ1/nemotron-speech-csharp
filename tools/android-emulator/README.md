# Android emulator for VoiceType.Uno

Develop and smoke-test the Uno android head without the physical phone.
All scripts target only `emulator-5554` (never a USB phone).

## Prerequisites (one-time)
- Windows Hypervisor Platform enabled (`emulator -accel-check` prints "WHPX ... usable").
  If missing, in an elevated shell: `Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform`, then reboot.
- JDK 21 (`C:\Program Files\Android\openjdk\jdk-21.0.8`).
- The system SDK in `C:\Program Files (x86)\Android` is read-only without admin, so the
  emulator lives in a separate SDK: `E:\Android\sdk` (AVD files in `E:\Android\avd`).
  Override with `VT_ANDROID_SDK` / `VT_ANDROID_AVD_HOME`.

## One-time setup
```powershell
$env:JAVA_HOME = 'C:\Program Files\Android\openjdk\jdk-21.0.8'
$sm = 'C:\Program Files (x86)\Android\android-sdk\cmdline-tools\latest\bin\sdkmanager.bat'
& $sm --sdk_root=E:\Android\sdk --licenses        # accept android-sdk-license
& $sm --sdk_root=E:\Android\sdk emulator platform-tools 'cmdline-tools;latest' 'system-images;android-35;google_apis;x86_64'
$env:ANDROID_SDK_ROOT='E:\Android\sdk'; $env:ANDROID_AVD_HOME='E:\Android\avd'
E:\Android\sdk\cmdline-tools\latest\bin\avdmanager.bat create avd -n voicetype-api35 -k 'system-images;android-35;google_apis;x86_64' -d pixel_6
```
Then edit `E:\Android\avd\voicetype-api35.avd\config.ini`:
`hw.ramSize=6144`, `hw.cpu.ncore=8`, `disk.dataPartition.size=24G`, `hw.gpu.enabled=yes`,
`hw.gpu.mode=host`, `hw.keyboard=yes`, `vm.heapSize=512` (Pixel 6 profile: 1080x2400, 420 dpi).

## Daily use
```powershell
tools\android-emulator\start-emulator.ps1            # windowed; -Headless for no window; -ColdBoot to skip snapshot
tools\android-emulator\install-app.ps1 -Launch       # latest Release *-Signed.apk, grants RECORD_AUDIO
tools\android-emulator\snap.ps1 -Path shot.png       # screenshot (screen is 1080x2400)
```
`start-emulator.ps1` waits for `sys.boot_completed`, keeps the screen on, and disables animations.
Build the APK first: `dotnet build apps/VoiceType.Uno/src/VoiceType.Uno -f net10.0-android -c Release`.

Fake battery/charging conditions (the emulator has no real battery):
```
adb -s emulator-5554 emu power ac on|off
adb -s emulator-5554 emu power capacity 15
adb -s emulator-5554 emu power status charging|discharging
```
Always pass `-s emulator-5554`; in Git Bash set `MSYS_NO_PATHCONV=1` for device paths.

## ABI findings
The Release APK ships both `arm64-v8a` and `x86_64` (203 files each), including
`libLiteRtLm.so`, `libonnxruntime.so`, `libonnxruntime-genai*.so`, `libSkiaSharp.so`.
So the hardware-accelerated `x86_64` image runs the app natively; no ARM translation needed.

## Known limits
- Emulator CPU is the PC's, so speed numbers are far better than the phone's; do not use them for performance conclusions.
- No real battery, thermals, vendor battery killers (EMUI/HarmonyOS), or phone GPU/NPU.
- Microphone: emulator uses host audio input (`-allow-host-audio`); enable "Virtual microphone uses host audio input" in Extended controls > Microphone.
- 6 GB RAM AVD needs ~6 GB free host RAM; lower `hw.ramSize` if the host is tight.
