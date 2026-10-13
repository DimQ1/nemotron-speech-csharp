using VoiceType.Uno.History;

namespace VoiceType.Uno.Services;

/// <summary>
/// Application settings persisted to settings.json.
/// Mirrors the WinUI model where the fields are platform-independent.
/// </summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public string AudioSource { get; set; } = "Mic";
    /// <summary>
    /// "Model storage folder": the ONE root of all models (<c>asr/</c>, <c>llm/</c>, <c>diarization/</c> inside it, see
    /// <see cref="ModelStorageLayout"/>). Empty means the app's default (<see cref="AppPaths.ModelsDir"/>).
    /// </summary>
    public string ModelsRootPath { get; set; } = "";
    /// <summary>
    /// A new model storage folder the user chose with "Move models": at the next start, before anything loads, the
    /// existing models move there (<see cref="ModelStorageMigrator.MoveRoot"/>) and this becomes <see cref="ModelsRootPath"/>.
    /// </summary>
    public string PendingModelsRoot { get; set; } = "";
    /// <summary>One line the Model storage block shows after a start-up move or migration that could not finish; cleared when shown once resolved.</summary>
    public string ModelStorageNote { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string ModelPath { get; set; } = "";
    /// <summary>The recordings folder (<c>sessions/</c> of the data root when empty). Changed from Settings › Storage.</summary>
    public string SessionsPath { get; set; } = "";
    /// <summary>
    /// A new recordings folder the user chose with "Move on restart": at the next start, before the history store opens, the
    /// recording folders move there (<see cref="RecordingStorageMigrator"/>) and this becomes <see cref="SessionsPath"/>.
    /// </summary>
    public string PendingSessionsPath { get; set; } = "";
    /// <summary>One line the Recordings card shows after a start-up move that could not finish.</summary>
    public string SessionsStorageNote { get; set; } = "";
    public string ExecutionProvider { get; set; } = "cpu";

    public bool UseVad { get; set; } = true;
    public double RepetitionPenalty { get; set; } = 1.1;

    public bool IsTextInjectionEnabled { get; set; } = true;
    public bool IsAutoScrollEnabled { get; set; } = true;
    public bool DisableInjectionOnFocusChange { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoStartRecognition { get; set; }
    public bool ClearTextOnModelOrSessionChange { get; set; } = true;
    public bool SaveAudioMp3 { get; set; }
    public bool FirstRunCompleted { get; set; }

    // ── Live translation (LiteRT-LM server, OpenAI-compatible endpoint) ─────
    /// <summary>Translate recognized text on the fly via a local LiteRT-LM server.</summary>
    public bool TranslationEnabled { get; set; }
    /// <summary>BCP-47 target language for live translation (e.g. "ru", "en").</summary>
    public string TranslationTargetLanguage { get; set; } = "ru";
    /// <summary>
    /// Translation engine: "native" loads the .litertlm model in-process (offline,
    /// no sidecar — works on Linux via LiteRtLmSharp linux-x64 natives); "http"
    /// talks to an external LiteRT-LM server. Default "native" with HTTP fallback
    /// when the model is not downloaded.
    /// </summary>
    public string TranslationBackend { get; set; } = "native";
    /// <summary>
    /// Compute backend for the native translation engine: "cpu" (XNNPACK, stable,
    /// default) or "gpu" (WebGPU delegate). Applies only to the "native" engine.
    /// </summary>
    public string TranslationComputeBackend { get; set; } = "cpu";
    /// <summary>
    /// Id of the language model (<see cref="LlmModelCatalog"/>) the shared engine runs for translation, chat,
    /// titles and summaries. Falls back to the default model while the selected one is not installed.
    /// </summary>
    public string LlmModelId { get; set; } = LlmModelCatalog.DefaultId;
    /// <summary>Base URL of the LiteRT-LM server (gemma-translator topology), used by the "http" backend.</summary>
    public string TranslationServerUrl { get; set; } = "http://localhost:9379";
    /// <summary>
    /// Optional extra system-prompt text appended to the built-in translation
    /// instruction (terminology, style, casing rules for Gemma 4). Empty by default.
    /// </summary>
    public string TranslationSystemPrompt { get; set; } = "";

    /// <summary>
    /// Key chord synthesized after setting the clipboard during text injection.
    /// "Ctrl+V" works for most apps; terminals often need "Ctrl+Shift+V" or
    /// "Shift+Insert".
    /// </summary>
    public string PasteChord { get; set; } = "Ctrl+V";

    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";
    public string MuteHotkey { get; set; } = "Ctrl+Shift+M";
    public string InjectTextHotkey { get; set; } = "Ctrl+Shift+I";

    // ── Recordings history ──────────────────────────────────────────────────
    private HistorySettings _history = new();

    /// <summary>
    /// History section (retention, storage cap, delete confirmation, inject dictations).
    /// Absent in older settings files, which then load with the defaults.
    /// </summary>
    public HistorySettings History
    {
        get => _history;
        set => _history = value ?? new HistorySettings();
    }

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone._history = _history.Clone();
        return clone;
    }
}
