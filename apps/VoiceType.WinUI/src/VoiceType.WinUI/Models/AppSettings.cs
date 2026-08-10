using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.Models;

/// <summary>
/// All application settings, persisted as JSON.
/// </summary>
public sealed class AppSettings
{
    // ── Engine ──────────────────────────────────────
    public string ModelsRootPath { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string ExecutionProvider { get; set; } = "cpu";
    public string Language { get; set; } = "auto";
    public bool UseVad { get; set; } = true;

    // ── Decoding quality ────────────────────────────
    public int NumBeams { get; set; } = 1;
    public double RepetitionPenalty { get; set; } = 1.1;

    // ── Capture ─────────────────────────────────────
    public string AudioSource { get; set; } = "Mic"; // Mic, Loopback, Mix

    // ── First-run onboarding ────────────────────────
    /// <summary>Set to true after the first-run wizard (model download + defaults) completes.
    /// When false, the app shows the onboarding wizard before the main window.</summary>
    public bool FirstRunCompleted { get; set; } = false;

    // ── Injection ───────────────────────────────────
    public InjectionMethod TextInjectionMethod { get; set; } = InjectionMethod.InputSimulator;
    /// <summary>Stop recognition on any keyboard/mouse input. Off by default so users can
    /// keep working (and keep dictating) without the recording cutting out on every keystroke.</summary>
    public bool StopOnAnyInput { get; set; } = false;
    public bool IsTextInjectionEnabled { get; set; } = true;
    /// <summary>When true, pauses text injection if the user switches to a different window during recording.</summary>
    public bool DisableInjectionOnFocusChange { get; set; } = true;

    // ── UI ──────────────────────────────────────────
    public bool IsAutoScrollEnabled { get; set; } = true;
    /// <summary>Automatically start recognition when the app launches.</summary>
    public bool AutoStartRecognition { get; set; } = false;
    /// <summary>Keep the main window always on top of other windows.</summary>
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>Keep recognized text visible when a session or model changes.</summary>
    public bool ClearTextOnModelOrSessionChange { get; set; } = true;

    // ── Sessions ────────────────────────────────────
    /// <summary>Whether to persist recognition sessions to disk. Off by default for privacy
    /// and to avoid unbounded disk growth; the user can opt in via Settings.</summary>
    public bool SaveSessions { get; set; } = false;
    public string SessionsPath { get; set; } = AppPaths.SessionsDir;
    public bool SaveAudioMp3 { get; set; } = false;

    // ── Post-processing ─────────────────────────────
    public bool PostProcessingEnabled { get; set; } = true;
    public List<PostProcessingRule> PostProcessingRules { get; set; } = new()
    {
        new() { Name = "Remove language tags (<ru-RU>, <en>, <auto>, etc.)", Pattern = @"<(?:[a-z]{2}(-[A-Z]{1,3})?|auto)>", Replacement = "" },
    };

    // ── Hotkey ──────────────────────────────────────
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";
    public string MuteHotkey { get; set; } = "Ctrl+Shift+M";
    /// <summary>Hotkey to manually inject the current recognized text into the focused window.</summary>
    public string InjectTextHotkey { get; set; } = "Ctrl+Shift+I";

    // ── Downloader ───────────────────────────────────
    public string DownloaderRepoId { get; set; } = "";
    public string DownloaderModelsRootPath { get; set; } = "";
    public string DownloaderSelectedFoldersRepoId { get; set; } = "";
    public List<string> DownloaderSelectedFolders { get; set; } = new();

    // ── Audio Mixer ─────────────────────────────────
    /// <summary>Mic volume (0.0 - 1.0). Applied in real-time during capture.</summary>
    public float MicVolume { get; set; } = 1.0f;
    /// <summary>Loopback volume (0.0 - 1.0). Applied in real-time during capture.</summary>
    public float LoopbackVolume { get; set; } = 1.0f;

    // ── Voice Intent Mode ───────────────────────────
    /// <summary>Settings for the optional voice-command (intent) mode.</summary>
    public VoiceIntentSettings VoiceIntents { get; set; } = new();

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.PostProcessingRules = PostProcessingRules
            .Select(rule => new PostProcessingRule
            {
                Name = rule.Name,
                Pattern = rule.Pattern,
                Replacement = rule.Replacement,
                Enabled = rule.Enabled
            })
            .ToList();
        clone.DownloaderSelectedFolders = DownloaderSelectedFolders.ToList();
            clone.VoiceIntents = VoiceIntents?.Clone() ?? new VoiceIntentSettings();
        return clone;
    }
}

public enum InjectionMethod { InputSimulator, SendInput, Clipboard }

    /// <summary>
    /// Settings for the optional Voice Intent Mode. When <see cref="Enabled"/> is
    /// false (default), the dictation pipeline remains unchanged and no intent
    /// model is loaded.
    /// </summary>
    public sealed class VoiceIntentSettings
    {
        /// <summary>Master toggle. False = mode fully off, no model loaded.</summary>
        public bool Enabled { get; set; }

        /// <summary>Path to the MiniLM ONNX model folder; empty = L0 heuristics only.</summary>
        public string IntentModelPath { get; set; } = "";

        /// <summary>Cosine / heuristic confidence threshold to accept a command.</summary>
        public float ConfidenceThreshold { get; set; } = 0.85f;

        /// <summary>Number of recent semantic scores that must reach the threshold.</summary>
        public int HysteresisM { get; set; } = 2;

        /// <summary>Number of scores retained by the semantic hysteresis window.</summary>
        public int HysteresisN { get; set; } = 3;

        /// <summary>Remember injection target windows and fields across sessions.</summary>
        public bool RememberTargets { get; set; } = true;

        /// <summary>Ask which remembered field to use when a command is ambiguous.</summary>
        public bool ConfirmTargetSwitch { get; set; } = true;

        /// <summary>Maximum remembered targets kept per process.</summary>
        public int HistoryPerProcess { get; set; } = 10;

        /// <summary>Maps process names to spoken aliases used for retargeting.</summary>
        public Dictionary<string, string[]> AppAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["code"] = new[] { "vs code", "visual studio code", "визуал студио код", "код" },
            ["notepad"] = new[] { "notepad", "блокнот" },
            ["notepad++"] = new[] { "notepad++", "notepad plus plus", "блокнот", "блокнот плюс плюс", "нотпад плюс плюс" },
        };

        /// <summary>Optional user-supplied intents.json overriding the built-in taxonomy.</summary>
        public string CustomIntentsFile { get; set; } = "";

        /// <summary>User-managed applications and executable paths available to target commands.</summary>
        public List<VoiceCommandApp> Applications { get; set; } = new();

        public VoiceIntentSettings Clone() => new()
        {
            Enabled = Enabled,
            IntentModelPath = IntentModelPath,
            ConfidenceThreshold = ConfidenceThreshold,
            HysteresisM = HysteresisM,
            HysteresisN = HysteresisN,
            RememberTargets = RememberTargets,
            ConfirmTargetSwitch = ConfirmTargetSwitch,
            HistoryPerProcess = HistoryPerProcess,
            AppAliases = new Dictionary<string, string[]>(AppAliases, StringComparer.OrdinalIgnoreCase)
            {
                // Clone the arrays as well: callers can edit aliases without
                // mutating an older settings snapshot.
            }.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            CustomIntentsFile = CustomIntentsFile,
            Applications = Applications.Select(app => app.Clone()).ToList(),
        };
    }

/// <summary>
/// A single post-processing rule: regex find-and-replace.
/// </summary>
public sealed class PostProcessingRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Replacement { get; set; } = "";
    public bool Enabled { get; set; } = true;
}
