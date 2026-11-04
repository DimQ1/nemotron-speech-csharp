# Store screenshot audio: SAPI voices rendered straight into 16 kHz mono WAV files (nothing is played through the speakers).
param([string]$Out = "$PSScriptRoot\audio")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $Out | Out-Null

$recordings = [ordered]@{
    'product-sync' = @(
        @('Microsoft David Desktop', 'Good morning everyone. Let us start with the release plan for version two. The new app is feature complete, and the remaining work is testing on Windows, Linux and Android.'),
        @('Microsoft Zira Desktop', 'Thanks. On my side, the installer is ready and the Store listing needs new screenshots. I would like to send the package for certification on Thursday.'),
        @('Microsoft David Desktop', 'That works. What about the model downloads? Last week some users asked whether their downloaded models will survive the update.'),
        @('Microsoft Zira Desktop', 'They will. The new version finds the models in the same folder and moves them into the new layout at the first start, so nobody has to download them again.'),
        @('Microsoft David Desktop', 'Great. Then the action items are: finish the screenshots, run the certification kit, and write the release notes in all languages.')
    )
    'customer-interview' = @(
        @('Microsoft Zira Desktop', 'Thank you for joining. How do you use voice typing in your daily work?'),
        @('Microsoft David Desktop', 'Mostly for email and meeting notes. I talk faster than I type, and I like that everything stays on my laptop. Our company does not allow cloud transcription.'),
        @('Microsoft Zira Desktop', 'What would make the app more useful for you?'),
        @('Microsoft David Desktop', 'Searching old recordings, and a short summary after each call. Being able to ask a question about last week''s meeting would save me a lot of time.')
    )
    'voice-note' = @(
        , @('Microsoft Zira Desktop', 'Note to self. Ideas for the blog post about offline speech recognition. First, explain why on device models matter for privacy. Second, compare the speed of the CPU and the GPU. Third, show how to type into any app with a global hotkey. Remember to add a short video at the end.')
    )
    'planerka' = @(
        @('Microsoft Pavel', 'Коллеги, доброе утро. Коротко о задачах на неделю. Мы заканчиваем тестирование новой версии и готовим публикацию в магазине.'),
        @('Microsoft Irina', 'Я обновлю описание приложения на шестнадцати языках и подготовлю новые скриншоты. К среде всё будет готово.'),
        @('Microsoft Pavel', 'Отлично. Тогда в четверг отправляем пакет на сертификацию.')
    )
}

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo 16000, ([System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen), ([System.Speech.AudioFormat.AudioChannel]::Mono)
foreach ($name in $recordings.Keys) {
    $prompt = New-Object System.Speech.Synthesis.PromptBuilder
    $prompt.AppendBreak([TimeSpan]::FromMilliseconds(600))
    foreach ($turn in $recordings[$name]) {
        $prompt.StartVoice($turn[0])
        $prompt.AppendText($turn[1])
        $prompt.EndVoice()
        $prompt.AppendBreak([TimeSpan]::FromMilliseconds(900))
    }
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $synth.Rate = -1
    $path = Join-Path $Out "$name.wav"
    $synth.SetOutputToWaveFile($path, $format)
    $synth.Speak($prompt)
    $synth.Dispose()
    '{0} {1:N1} s' -f $path, (((Get-Item $path).Length - 44) / 32000)
}
