---
name: "VoiceType WPF Project Instructions"
description: "Use when working on the VoiceType WPF dictation application, MVVM, global input hooks, text injection, recording, or settings."
applyTo: "VoiceType/**"
---

# VoiceType WPF Rules

## Architecture

- Keep WPF views thin. Recognition, capture, text injection, global hooks, persistence, and post-processing belong in services or ViewModels.
- Preserve the provider split: `VoiceType` uses `SpeechLib.Audio.NAudio3`, while the CLI compatibility path uses `SpeechLib.Audio.NAudio2`.
- Use `IAudioSourceFactory` for capture selection instead of constructing NAudio sources inside the recognition loop.
- Keep model lifecycle separate from capture lifecycle so a loaded model can be reused across recognition sessions.

## MVVM and threading

- Preserve the existing manual `INotifyPropertyChanged`, `RelayCommand`, and `AsyncRelayCommand` conventions.
- Dispatch ViewModel property and collection updates through `Application.Current.Dispatcher` when they originate from capture, recognition, hook, or background service threads.
- Handle expected failures in ViewModels and expose a user-visible status; do not depend on `AsyncRelayCommand` to surface exceptions.
- WPF bindings are TwoWay by default. Bind computed or read-only properties with `Mode=OneWay`.
- Use file-scoped namespaces, nullable reference types, implicit usings, and `_camelCase` private fields.

## Audio and recording

- Keep audio batches bounded and use the shared `IAudioSource` contract.
- Dispose capture sources, recorders, synchronization primitives, and recognizers deterministically.
- MP3 recording is optional and must not be created on the normal recognition path when disabled.
- Treat VAD as model input processing; muting capture is the explicit path that discards audio and avoids recognition work.

## Build and verify

```powershell
dotnet build NemotronSpeech.slnx -c Release -p:GpuArch=CPU
dotnet test VoiceType.Tests/VoiceType.Tests.csproj --filter "FullyQualifiedName~Unit_"
```

Do not add `<UseWPF>true</UseWPF>` to `VoiceType.Tests.csproj`. Do not commit `bin`, `obj`, downloaded models, session data, or generated audio.
