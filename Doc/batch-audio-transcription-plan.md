# Batch Audio Transcription Window — Implementation Plan

## Branch: `feat/diarization-voice-type`
## Status: Planning

## Overview — Two Major Changes

### A. REMOVE streaming diarization
Diarization moves exclusively to file/batch mode. The real-time VoiceType window keeps plain ASR + text injection — no diarization overhead, no extra window, no extra thread.

### B. NEW BatchAudioWindow
Standalone window for batch processing audio files with ASR + diarization. Includes model selection, audio playback, and transcript viewer with 3 display modes.

---

## Architecture

```
MainWindow (streaming — DIARIZATION REMOVED)
    │
    ├── 🎤 Start/Stop (real-time ASR, plain text only)
    ├── 📝 Text injection (unchanged)
    ├── ⚙ Settings (unchanged)
    └── [NEW] 📂 "Batch Audio" button → BatchAudioWindow

BatchAudioWindow (NEW)
    │
    ├── File list panel
    │   ├── [+ Add Files] [+ Add Folder] [✕ Remove] [🗑 Clear]
    │   └── ListBox: file name | duration | status icon
    │
    ├── Settings panel
    │   ├── ASR Model: [dropdown — scanned from models-onnx/]
    │   ├── Language: [auto/en/ru/de...]
    │   ├── ☑ Enable speaker diarization
    │   ├── Diarization model: [path textbox] [Browse...]
    │   ├── Parallel jobs: [2] (spinner 1-8)
    │   ├── Output directory: [path textbox] [Browse...]
    │   └── Export format: [TXT ▾ | SRT | VTT]
    │
    ├── Progress panel (per-file progress bars + status)
    │
    ├── [▶ Start Batch]  [⏹ Cancel]  [📁 Open Output Folder]
    │
    └── Transcript Viewer (after double-clicking a completed file)
        ├── Audio playback bar: ▶ ⏸ ⏹ | slider | time
        └── Tabs: [Plain Text | Word Timings | Speakers]
```

---

## Data Flow

```
User adds files → List<AudioFileJob>
    │
    ▼
Click "▶ Start Batch" → BatchTranscriptionService
    │
    ├── SemaphoreSlim(parallelism) gate
    │
    ├── For each file (parallel up to N):
    │   ├── 1. Create ModelSession (per-job, ORT not thread-safe)
    │   ├── 2. If diarization: await _diarizationLock → SortformerDiarizationService.Diarize()
    │   ├── 3. Transcriber.RunFile(audio, asr, wordTimestamps: true, out timings, diarization)
    │   ├── 4. If diarization: DiarizationMergeService.Merge(timings, segments)
    │   ├── 5. Export formatted result (TXT/SRT/VTT)
    │   ├── 6. Store AudioFileJob.PlainText, .DiarizedText, .WordTimings, .SpeakerUtterances
    │   └── 7. Report progress → UI
    │
    └── All jobs complete

User double-clicks completed file → Transcript Viewer:
    ├── Loads job results
    ├── NAudio player for audio playback
    ├── Seek sync: click word/segment → audio position
    └── Tab switching: Plain | Word Timings | Speakers
```

---

## Part A — REMOVE Streaming Diarization

### Files to DELETE
| File | Reason |
|---|---|
| `VoiceType/Services/StreamingDiarizationService.cs` | Only for streaming mode |
| `VoiceType/Views/DiarizationWindow.xaml` | Streaming diarization preview window |
| `VoiceType/Views/DiarizationWindow.xaml.cs` | Code-behind |
| `VoiceType/ViewModels/DiarizationViewModel.cs` | ViewModel for diarization window |

### Files to MODIFY (clean up diarization code)
| File | What to remove |
|---|---|
| `RecognitionService.cs` | `_diarization`, `_diarizationInitLock`, `_diarizationInitialized`, `InitDiarization()`, `StructuredUtterancesReady` event, diarization `AppendAudio`/`AddTimedText` in `ProcessLoop()`, diarization `StopAsync` in `ProcessLoop()` |
| `MainViewModel.cs` | `_diarizationWindow`, `StructuredFloatingText`, `DisplayText`, `OnStructuredUtterancesReady()`, diarization window opening in `Start()` |
| `MainWindow.xaml` | Already reverted to `FloatingText` |
| `AppSettings.cs` | `EnableDiarization`, `DiarizationModelPath`, `DiarizationWindowSeconds`, `DiarizationStepSeconds` |
| `SettingsWindow.xaml` | Diarization GroupBox section (Speaker Diarization) |
| `SettingsViewModel.cs` | Diarization properties, `BrowseDiarizationModelCommand`, `BuildSettings` diarization fields |

### Files KEPT (used by file-mode diarization)
- ✅ `SortformerDiarizationService.cs`
- ✅ `DiarizationMergeService.cs`
- ✅ `DiarizedUtterance.cs`
- ✅ `DiarizationSegment.cs`
- ✅ `IDiarizationService.cs`
- ✅ `MelSpectrogram.cs`

---

## Part B — NEW BatchAudioWindow

### New Files
| # | File | Purpose |
|---|---|---|
| 1 | `VoiceType/Views/BatchAudioWindow.xaml` | WPF window layout |
| 2 | `VoiceType/Views/BatchAudioWindow.xaml.cs` | Code-behind |
| 3 | `VoiceType/ViewModels/BatchAudioViewModel.cs` | ViewModel: file list, settings, commands, playback |
| 4 | `VoiceType/Services/BatchTranscriptionService.cs` | Parallel job runner with SemaphoreSlim |
| 5 | `VoiceType/Models/AudioFileJob.cs` | Model: path, status, result, timings, segments |
| 6 | `VoiceType/Services/SubtitleExporter.cs` | TXT/SRT/VTT formatters |
| 7 | `VoiceType/Services/AudioPlaybackService.cs` | NAudio-based playback with position tracking |

### Modified Files
| # | File | Change |
|---|---|---|
| 8 | `VoiceType/Views/MainWindow.xaml` | Add "📂 Batch" button |
| 9 | `VoiceType/ViewModels/MainViewModel.cs` | Add `OpenBatchWindowCommand` |
| 10 | `VoiceType/Models/AppSettings.cs` | Add batch settings fields |

---

## AudioFileJob Model

```csharp
public sealed class AudioFileJob : INotifyPropertyChanged
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public string DurationDisplay { get; set; } = "";    // "3:42"
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = "Queued";       // Queued, Processing, Done, Error
    public int ProgressPercent { get; set; }             // 0-100

    // Results (populated after processing)
    public string? PlainText { get; set; }
    public string? DiarizedText { get; set; }
    public List<WordTiming>? WordTimings { get; set; }
    public List<DiarizedUtterance>? SpeakerUtterances { get; set; }

    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }
}
```

## BatchAudioViewModel — Key Commands

| Command | Action |
|---|---|
| `AddFilesCommand` | Open file dialog → add .wav/.mp3/.flac/.m4a/.ogg files |
| `AddFolderCommand` | Open folder dialog → add all audio files recursively |
| `RemoveSelectedCommand` | Remove selected files from list |
| `ClearAllCommand` | Clear entire file list |
| `StartBatchCommand` | Begin processing all queued files |
| `CancelBatchCommand` | Cancel via CancellationToken |
| `OpenOutputDirCommand` | Open output folder in Explorer |
| `BrowseOutputDirCommand` | Folder browser dialog |
| `BrowseDiarizationModelCommand` | File browser for sortformer.onnx |
| `ViewFileCommand` | Open transcript viewer for selected completed file |
| `PlayPauseCommand` | Toggle audio playback |
| `StopPlaybackCommand` | Stop playback and reset position |
| `SeekCommand` | Seek audio to clicked word/segment position |

---

## Transcript Viewer — 3 Tabs

### Tab 1: Plain Text
- Read-only TextBox, selectable, copyable
- If diarization: speaker labels prepended
- If no diarization: raw transcript

### Tab 2: Word Timings
```
┌──────────────────────────────────────────┐
│ 00:00.0 → 00:00.3  hello                │
│ 00:00.4 → 00:00.7  world  ← highlighted │
│ 00:00.8 → 00:01.2  this                 │
│ 00:01.3 → 00:01.8  is                   │
│ 00:01.9 → 00:02.4  a                    │
│ 00:02.5 → 00:03.1  test                 │
└──────────────────────────────────────────┘
```
- Each row: `start → end  word`
- Click on row → seek audio to word start
- Currently playing word: highlighted background
- Auto-scroll to playing word during playback

### Tab 3: Speakers (diarization only)
```
┌──────────────────────────────────────────┐
│ ── Speaker 01 (00:00.0 → 00:05.3) ──    │
│ hello world this is a test              │
│                                          │
│ ── Speaker 02 (00:05.3 → 00:12.7) ──    │
│ yes I agree completely                  │
└──────────────────────────────────────────┘
```
- Each block: speaker header with time range + full text
- Click on block → seek audio to segment start
- Currently playing segment: highlighted header

---

## Audio Playback Service

```csharp
public sealed class AudioPlaybackService : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;

    public event Action<TimeSpan>? PositionChanged;   // fires every 100ms
    public event Action? PlaybackEnded;

    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; }
    public bool IsPlaying { get; }

    public void Open(string filePath);
    public void Play();
    public void Pause();
    public void Stop();
    public void Seek(TimeSpan position);
    public void Dispose();
}
```

---

## AppSettings — New Batch Fields

```csharp
// ── Batch Transcription ──────────────────────────
public string BatchOutputDirectory { get; set; } = "";
public int BatchParallelism { get; set; } = 2;
public string BatchExportFormat { get; set; } = "txt";
public bool BatchEnableDiarization { get; set; } = false;
public string BatchDiarizationModelPath { get; set; } = "";
public string BatchAsrModelPath { get; set; } = "";
public string BatchLanguage { get; set; } = "auto";
```

---

## Full UI Layout

```
┌──────────────────────────────────────────────────────────────────────┐
│ 📂 Batch Audio Transcription                            [_] [□] [✕] │
├──────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────────┐ ┌──────────────────────────────────────┐│
│ │ FILES                    │ │ SETTINGS                             ││
│ │                          │ │                                      ││
│ │ ┌──────────────────────┐ │ │ ASR Model:                           ││
│ │ │ ✓ interview.wav 5:42 │ │ │ [nemotron-3.5-asr...int8-cpu ▾]     ││
│ │ │ ✓ meeting.mp3  12:15 │ │ │ Language:  [auto ▾]                 ││
│ │ │ ⏳ lecture.flac 45:00│ │ │                                      ││
│ │ │ ⏳ notes.wav    2:30 │ │ │ ☑ Enable speaker diarization        ││
│ │ │ ⏳ call.m4a      8:05│ │ │ Diarization model:                   ││
│ │ │ ❌ error.wav    1:00 │ │ │ [models/sortformer.onnx...] [Browse] ││
│ │ └──────────────────────┘ │ │                                      ││
│ │                          │ │ Parallel jobs:  [2] [▲][▼]           ││
│ │ [+ Add Files]            │ │                                      ││
│ │ [📁 Add Folder]          │ │ Output directory:                    ││
│ │ [✕ Remove] [🗑 Clear]   │ │ [C:\Users\...\Transcripts] [Browse]  ││
│ │                          │ │                                      ││
│ │                          │ │ Export format:                       ││
│ │                          │ │ [TXT ▾ | SRT | VTT]                 ││
│ └──────────────────────────┘ └──────────────────────────────────────┘│
│                                                                       │
│ ┌───────────────────────────────────────────────────────────────────┐│
│ │ PROGRESS                                                          ││
│ │ interview.wav  ██████████████████████ 100% ✅ Done                ││
│ │ meeting.mp3    ██████████░░░░░░░░░░░░  58% 🔄 Processing...      ││
│ │ lecture.flac   ⏳ Queued                                          ││
│ └───────────────────────────────────────────────────────────────────┘│
│                                                                       │
│ [▶ Start Batch]  [⏹ Cancel]  [📁 Open Output Folder]                 │
│                                                                       │
│ ═══════════════════════ TRANSCRIPT VIEWER ═══════════════════════════ │
│ (visible after double-clicking a completed ✅ file)                   │
│                                                                       │
│ ┌───────────────────────────────────────────────────────────────────┐│
│ │ ▶  ⏸  ⏹   ├───────────────●──────────────────┤   01:23.5 / 05:42 ││
│ └───────────────────────────────────────────────────────────────────┘│
│                                                                       │
│ [Plain Text]  [Word Timings]  [Speakers]                              │
│ ┌───────────────────────────────────────────────────────────────────┐│
│ │ 00:00.0 → 00:00.3  hello                                         ││
│ │ 00:00.4 → 00:00.7  world         ← 🔵 highlighted (now playing)  ││
│ │ 00:00.8 → 00:01.2  this                                          ││
│ │ 00:01.3 → 00:01.8  is                                            ││
│ │ 00:01.9 → 00:02.4  a                                             ││
│ │ 00:02.5 → 00:03.1  test                                          ││
│ └───────────────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────────────┘
```

---

## Export Formats

### TXT
```
hello world this is a test of the speech recognition system
```
With diarization:
```
SPEAKER_00: hello world this is a test
SPEAKER_01: of the speech recognition system
```

### SRT
```
1
00:00:00,000 --> 00:00:02,500
hello world this is a test

2
00:00:02,500 --> 00:00:05,000
of the speech recognition system
```
With diarization: `[SPEAKER_00] hello world...`

### VTT
```
WEBVTT

00:00:00.000 --> 00:00:02.500
hello world this is a test

00:00:02.500 --> 00:00:05.000
of the speech recognition system
```

---

## Key Design Decisions

| # | Decision | Reason |
|---|---|---|
| 1 | Per-job ASR sessions | ORT `InferenceSession` not thread-safe |
| 2 | Shared diarization + `SemaphoreSlim(1)` | ORT not thread-safe; 129MB model loaded once |
| 3 | Diarization removed from streaming | User explicitly requested; diarization moves to batch only |
| 4 | Audio via NAudio `AudioFileReader` | Supports WAV/MP3/FLAC/M4A/OGG |
| 5 | 3 transcript views | Plain text (fast), word timings (precision), speakers (context) |
| 6 | Click-to-seek sync | Bidirectional: click word → seek audio; playback → highlight word |

---

## Implementation Phases

### Phase 0 — REMOVE streaming diarization
- [ ] Delete: `StreamingDiarizationService.cs`, `DiarizationWindow.xaml/.cs`, `DiarizationViewModel.cs`
- [ ] Clean up: `RecognitionService`, `MainViewModel`, `AppSettings`, `SettingsWindow`, `SettingsViewModel`
- [ ] Build + run 95 tests — ensure no regressions

### Phase 1 — Models + SubtitleExporter + AudioPlaybackService
- [ ] `AudioFileJob.cs` model
- [ ] `SubtitleExporter.cs` — TXT, SRT, VTT formatters
- [ ] `AudioPlaybackService.cs` — NAudio wrapper
- [ ] Unit tests for SRT/VTT timestamp formatting + playback seek

### Phase 2 — BatchTranscriptionService
- [ ] SemaphoreSlim-based parallel runner
- [ ] Per-job `ModelSession` creation + disposal
- [ ] Shared `SortformerDiarizationService` + lock
- [ ] `IProgress<AudioFileJob>` reporting
- [ ] `CancellationToken` support

### Phase 3 — BatchAudioViewModel
- [ ] `ObservableCollection<AudioFileJob>` with selection
- [ ] File add/remove/clear commands
- [ ] Start/Cancel batch commands
- [ ] Settings bindings (model dropdown, diarization toggle, parallelism, output dir, format)
- [ ] Model dropdown population (scan models-onnx folder)
- [ ] Transcript viewer tab switching
- [ ] Playback controls (play/pause/stop/seek)
- [ ] Bidirectional sync: word click ↔ audio position

### Phase 4 — BatchAudioWindow (XAML + code-behind)
- [ ] Two-column layout: files (left) + settings (right)
- [ ] Progress panel at bottom
- [ ] Audio player bar
- [ ] Transcript viewer with 3 tabs
- [ ] Custom window chrome (drag, min, max, close)

### Phase 5 — MainWindow integration
- [ ] "📂 Batch" button in title bar
- [ ] `OpenBatchWindowCommand` in MainViewModel
- [ ] Batch settings in AppSettings

### Phase 6 — Polish
- [ ] Drag-and-drop files onto file list
- [ ] Remember window position/size
- [ ] Audio duration pre-scan (show before processing)
- [ ] Dark theme styling
- [ ] Keyboard shortcuts (Space=play/pause, Left/Right=seek)
