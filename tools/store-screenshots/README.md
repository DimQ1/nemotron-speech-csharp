# Store screenshots

How the Microsoft Store screenshots of VoiceType 2.0 (`docs/store/imports/v2.0.0/screenshots/`) were made: real
recognition, speaker detection and Gemma output in the installed Release package, without playing or capturing any audio.

1. `pwsh make-audio.ps1 [-Out <dir>]`: four recordings rendered by SAPI voices straight into 16 kHz mono WAV files
   (two speakers in two of them, one in Russian). Edit the texts in the script.
2. `dotnet run seed.cs -- <sessions dir> <asr models dir> <model folder> <audio dir>`: recognizes each WAV with the real model
   through `OfflineTranscriber` (the History "recognize again" path) and writes the recordings with `RecordingStore`.
   Example: `dotnet run seed.cs -- ..\..\work\store-shots\data\sessions ..\..\models\asr parakeet-tdt-0.6b-v3-onnx-int4 ..\..\work\store-shots\audio`.
3. Install the preview package (`apps/VoiceType.Uno/src/VoiceType.Uno/build-store-msix.ps1 -Version x.y.z -Preview -Install`),
   copy the recordings into `%LOCALAPPDATA%\Packages\DimQ1.VoiceType.UnoPreview_310ax279fjzmt\LocalCache\Local\VoiceType\sessions`,
   and copy (never link) the models into `models\asr|llm|diarization` there. Start it from `shell:AppsFolder\DimQ1.VoiceType.UnoPreview_310ax279fjzmt!App`.
4. `. .\shot.ps1` gives `Get-VoiceTypeWindow`, `Set-ClientSize $h 1920 1080`, `Invoke-ByName $h '<UIA name>'`,
   `Get-Names $h` and `Save-Client $h <file.png>` (PrintWindow: the window may be covered; restore it if it is minimized).
   The app was driven through UI Automation: Detect speakers, rename speakers, Ask Gemma with two recordings, Translate to
   English, Manual input + Live translation to German, Settings > Typing into apps / Keyboard shortcuts, Models.

Keep the preview package and its data apart from a real VoiceType install: a different identity is a different data folder.
Remove it with `Get-AppxPackage DimQ1.VoiceType.UnoPreview | Remove-AppxPackage` (this also deletes its data folder).
