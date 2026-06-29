---
name: nemotron-ui
description: "Use when: working on NemotronSpeech UI/console output — real-time transcription display, progress indicators, console formatting for streaming ASR, or adding GUI/web frontend to the speech recognition pipeline."
---

# NemotronSpeech UI Patterns

## Current State

The project is a **console application** with no GUI framework. UI patterns are console-output conventions for real-time streaming ASR transcription.

---

## Pattern 1: Streaming Console Output

### File: `ModelSession.cs` — `DecodeTokens()`

**Character-by-character real-time output:**

```csharp
public string DecodeTokens()
{
    var text = "";
    while (!_generator.IsDone())
    {
        _generator.GenerateNextToken();
        var tokens = _generator.GetNextTokens();
        if (tokens.Length > 0)
        {
            var t = _tokenizerStream.Decode(tokens[0]);
            if (!string.IsNullOrEmpty(t)) { Console.Write(t); text += t; }
        }
    }
    return text;
}
```

**Pattern:** Write each decoded token fragment immediately to `Console` — gives real-time streaming feel. Accumulate in `text` for final display/logging.

---

## Pattern 2: Structured Console Output Sections

### File: `Transcriber.cs`, `Program.cs`

**Three-tier output hierarchy:**

```
┌─ Header block (model info, capabilities)
│    EP requested: cuda
│    Available:    cpu, cuda, dml
│    Language: ru -> lang_id=11
│    Model: single-language (no lang_id needed)
│    Use VAD: true
│
├─ Separator: ──────────────────────────────────────────── (60 chars)
│
├─ Body (streaming transcription appears here)
│    [Listening...]
│    Привет, как дела?...
│
├─ Separator: ============================================ (60 chars)
│
└─ Footer block (final transcript)
     Привет, как дела? Сегодня хорошая погода.
    ============================================================
```

**Rules:**
- `-` (dash) separator = section divider within output
- `=` (equals) separator = final result boundary
- 60-character width for both separators
- Info lines use 2-space indent prefix

---

## Pattern 3: Status Indicators

### Real-time capture status:

```csharp
Console.WriteLine("  [Listening...]");          // Waiting for audio
Console.WriteLine($"  Capture: {label}");        // Active source label
Console.WriteLine($"  Sample rate: {rate} Hz, Chunk: {samples} samples ({ms} ms)");
Console.WriteLine("  Press Ctrl+C to stop. Speaking...");
```

### Warning format (non-blocking):
```csharp
Console.WriteLine($"  Warning: lang_id not set ({e.Message})");
Console.WriteLine($"  VAD: disabled ({e.Message})");
Console.WriteLine($"  Warning: Unknown language '{langArg}'.");
```

**Rules:**
- Warnings are prefixed with `  Warning: ` (2-space + bold keyword)
- Errors that are fatal → `Console.WriteLine($"Error: {ex.Message}")` + usage
- VAD status echoed back: `"Use VAD: " + session.VadStatus`

---

## Pattern 4: Live Capture UI State Machine

### File: `Transcriber.cs` — `RunLive()`

```
State 1: Warmup
  └─► Feed silent chunk (JIT compilation)
  
State 2: Capture active
  ├─► Producer thread: capture audio → enqueue batches
  ├─► Consumer loop: dequeue → process → decode → Console.Write()
  └─► Timeout detection: 1.5s silence → exit loop

State 3: Flush + Final
  ├─► Flush remaining processor buffer
  ├─► Decode final tokens
  └─► Print final transcript with === separators
```

**Silence detection:** `(DateTime.UtcNow - lastAudio).TotalSeconds > 1.5` — if no audio processed in 1.5s and buffer is empty, stop.

**Thread coordination:**
- `isRunning` flag (passed by `ref`) — shared between producer and consumer
- `ManualResetEventSlim` — signals new data (not used for polling, just for wake-up)
- `ConcurrentQueueWrapper.IsEmpty` — termination condition
- Producer sets `isRunning = false` on `RecordingStopped`
- Consumer breaks when `!isRunning && buffer.IsEmpty && silence_timeout`

---

## Pattern 5: Multi-Mode UI Labels

### File: `Program.cs`

**Capture mode → human-readable label mapping:**

```csharp
var label = opts.Mode switch
{
    CaptureMode.Mic      => "Microphone",
    CaptureMode.Loopback => "System audio (loopback)",
    CaptureMode.Mix      => "Microphone + System audio (mixed)",
    _ => ""
};
```

**Pattern:** Use `switch` expression for mode → display string. Always provide user-friendly names (not enum values).

---

## Pattern 6: File Mode vs Live Mode UI

### File mode (batch):
```
Audio: 12.5s (200000 samples)
────────────────────────────────────────────────────────────
<streaming transcription>
============================================================
  Full transcript here.
============================================================
```

- Duration shown as seconds with 1 decimal
- Sample count in parentheses
- No "Listening..." indicator

### Live mode (streaming):
```
  Capture: Microphone
  Sample rate: 16000 Hz, Chunk: 2560 samples (160 ms)
  Press Ctrl+C to stop. Speaking...
────────────────────────────────────────────────────────────
  [Listening...]
<streaming transcription>
============================================================
  Full transcript here.
============================================================
```

- Source label shown
- Chunk size in both samples and ms
- Interactive instructions shown
- `[Listening...]` indicator

---

## Future UI Migration Guidelines

When adding a GUI/web frontend:

### Abstraction points
- Replace `Console.Write()` in `DecodeTokens()` with an `IObservable<string>` or event
- `Transcriber.RunLive()` → extract consumer loop into async enumerable (`IAsyncEnumerable<string>`)
- Status messages → structured logging (`ILogger<T>`) instead of `Console.WriteLine()`

### Real-time display considerations
- Token-level updates means ~20-50ms per character — debounce for GUI rendering
- Use `Channel<T>` or `System.Threading.Channels` instead of `ConcurrentQueue` for async
- `ManualResetEventSlim` → `TaskCompletionSource` or `SemaphoreSlim`

### Suggested tech stack for web UI
- **Frontend:** Angular (signals, standalone components)
- **Backend:** ASP.NET Core SignalR for real-time token streaming
- **Transport:** WebSocket with JSON frames: `{ "type": "token"|"status"|"final", "text": "..." }`
- **Audio capture:** `navigator.mediaDevices.getUserMedia({ audio: true })` → MediaRecorder → WebSocket

---

## Console Formatting Conventions Summary

| Element | Format | Example |
|---------|--------|---------|
| Info line | `  Key: value` | `  EP requested: cuda` |
| Warning | `  Warning: message` | `  Warning: Unknown language 'xx'` |
| Error | `Error: message` | `Error: Model path not found` |
| Section separator | `─` × 60 | `──────────────────...` |
| Final separator | `=` × 60 | `══════════════════...` |
| Status indicator | `  [Status]` | `  [Listening...]` |
| Duration | `{seconds:F1}s` | `12.5s` |
| Chunk info | `{samples} samples ({ms:F0} ms)` | `2560 samples (160 ms)` |
