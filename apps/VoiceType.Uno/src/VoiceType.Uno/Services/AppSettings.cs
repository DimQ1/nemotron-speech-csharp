namespace VoiceType.Uno.Services;

/// <summary>
/// Application settings persisted to settings.json.
/// Mirrors the WinUI model where the fields are platform-independent.
/// </summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public string AudioSource { get; set; } = "Microphone";
    public string ModelsRootPath { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string SessionsPath { get; set; } = "";
    public string ExecutionProvider { get; set; } = "CPU";

    public bool UseVad { get; set; } = true;
    public int NumBeams { get; set; } = 1;
    public float RepetitionPenalty { get; set; } = 1.0f;

    public bool IsTextInjectionEnabled { get; set; }
    public bool IsAutoScrollEnabled { get; set; } = true;
    public bool DisableInjectionOnFocusChange { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool AutoStartRecognition { get; set; }
    public bool ClearTextOnModelOrSessionChange { get; set; } = true;
    public bool SaveAudioMp3 { get; set; }
    public bool FirstRunCompleted { get; set; }

    public string ToggleHotkey { get; set; } = "";
    public string MuteHotkey { get; set; } = "";
    public string InjectTextHotkey { get; set; } = "";

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
