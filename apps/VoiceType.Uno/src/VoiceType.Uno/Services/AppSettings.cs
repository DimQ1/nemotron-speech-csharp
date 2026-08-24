namespace VoiceType.Uno.Services;

/// <summary>
/// Application settings persisted to settings.json.
/// Mirrors the WinUI model where the fields are platform-independent.
/// </summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public string AudioSource { get; set; } = "Mic";
    public string ModelsRootPath { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string SessionsPath { get; set; } = "";
    public string ExecutionProvider { get; set; } = "cpu";

    public bool UseVad { get; set; } = true;
    public int NumBeams { get; set; } = 1;
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
    /// <summary>Base URL of the LiteRT-LM server (gemma-translator topology), used by the "http" backend.</summary>
    public string TranslationServerUrl { get; set; } = "http://localhost:9379";

    /// <summary>
    /// Key chord synthesized after setting the clipboard during text injection.
    /// "Ctrl+V" works for most apps; terminals often need "Ctrl+Shift+V" or
    /// "Shift+Insert".
    /// </summary>
    public string PasteChord { get; set; } = "Ctrl+V";

    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";
    public string MuteHotkey { get; set; } = "Ctrl+Shift+M";
    public string InjectTextHotkey { get; set; } = "Ctrl+Shift+I";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
