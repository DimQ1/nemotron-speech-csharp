namespace VoiceType.WinUI.Interfaces;

using VoiceType.WinUI.Models;

/// <summary>Core recognition service abstraction.</summary>
public interface IRecognitionService : IDisposable
{
    bool IsRunning { get; }
    bool IsMuted { get; }
    int SampleRate { get; }
    string AccumulatedText { get; }

    /// <summary>Current model lifecycle state.</summary>
    ModelState ModelState { get; }
    string? LoadedModelPath { get; }

    event Action<string>? PartialResult;
    event Action<string>? FinalResult;

    /// <summary>Fires when a completed utterance is finalized via blank-based endpointing.</summary>
    event Action<string>? UtteranceFinalized;

    event Action? Stopped;

    /// <summary>Fires when audio capture fails to start (missing microphone, broken loopback, etc.).</summary>
    event Action<string>? CaptureError;

    /// <summary>Fires when <see cref="ModelState"/> changes.</summary>
    event Action<ModelState>? ModelStateChanged;

    /// <summary>
    /// Load the ASR model into memory asynchronously.
    /// Does NOT start audio capture. Safe to call multiple times —
    /// returns immediately if already loaded or loading.
    /// </summary>
    Task LoadModelAsync(AppSettings settings);

    /// <summary>
    /// Unload the model from memory. Safe to call when already unloaded.
    /// </summary>
    void UnloadModel();

    /// <summary>
    /// Start audio capture and recognition.
    /// Model must be in <see cref="ModelState.Loaded"/> state.
    /// </summary>
    void Start(AppSettings settings);

    /// <summary>
    /// Stop audio capture and finalize recognition.
    /// Model stays loaded in memory.
    /// </summary>
    void Stop();

    /// <summary>
    /// Stop capture and await all per-session processing before cleanup.
    /// </summary>
    Task StopAndCleanupAsync();

    void SetMuted(bool muted);
    string? SaveAudio(string fileNameBase);

    /// <summary>
    /// Change the recognition language at runtime without reloading the model.
    /// Only works for multilingual models.
    /// </summary>
    /// <param name="language">BCP-47 language code (e.g. "en", "ru") or "auto".</param>
    void SetLanguage(string language);

    /// <summary>
    /// Apply settings that are safe to change while the model remains loaded.
    /// </summary>
    void ApplyRuntimeSettings(AppSettings settings);
}
