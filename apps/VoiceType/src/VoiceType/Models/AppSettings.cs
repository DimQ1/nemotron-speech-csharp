using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoiceType.Services;

namespace VoiceType.Models;

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

    // ── Injection ───────────────────────────────────
    public InjectionMethod TextInjectionMethod { get; set; } = InjectionMethod.InputSimulator;
    public bool StopOnAnyInput { get; set; } = true;
    public bool IsTextInjectionEnabled { get; set; } = true;
    /// <summary>When true, pauses text injection if the user switches to a different window during recording.</summary>
    public bool DisableInjectionOnFocusChange { get; set; } = true;

    // ── UI ──────────────────────────────────────────
    public bool IsAutoScrollEnabled { get; set; } = true;
    /// <summary>Automatically start recognition when the app launches.</summary>
    public bool AutoStartRecognition { get; set; } = false;

    // ── Sessions ────────────────────────────────────
    public bool SaveSessions { get; set; } = true;
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

    // ── Voice Intent Mode ─────────────────────────────
    /// <summary>Settings for the optional voice-command (intent) mode.</summary>
    public VoiceIntentSettings VoiceIntents { get; set; } = new();
}

public enum InjectionMethod { InputSimulator, SendInput, Clipboard }

/// <summary>
/// Settings for the optional Voice Intent Mode. When <see cref="Enabled"/> is
/// false (default) the router is never created and the dictation pipeline is
/// unchanged. All fields have safe defaults so existing settings.json loads
/// without migration.
/// </summary>
public sealed class VoiceIntentSettings
{
    /// <summary>Master toggle. False = mode fully off, no model loaded.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Path to the MiniLM ONNX model folder; empty = L0 heuristics only.</summary>
    public string IntentModelPath { get; set; } = "";

    /// <summary>Cosine / heuristic confidence threshold to accept a command.</summary>
    public float ConfidenceThreshold { get; set; } = 0.85f;

    /// <summary>Hysteresis: M of last N scores must reach threshold to enter state.</summary>
    public int HysteresisM { get; set; } = 2;
    public int HysteresisN { get; set; } = 3;

    /// <summary>Remember injection target windows / fields across sessions.</summary>
    public bool RememberTargets { get; set; } = true;

    /// <summary>Ask (dialog) which remembered field to type into when ambiguous.</summary>
    public bool ConfirmTargetSwitch { get; set; } = true;

    /// <summary>Max remembered targets kept per process (LRU).</summary>
    public int HistoryPerProcess { get; set; } = 10;

    /// <summary>Maps process name (lower, no .exe) to spoken aliases for retargeting.</summary>
    public Dictionary<string, string[]> AppAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = new[] { "vs code", "visual studio code", "визуал студио код", "код" },
        ["notepad"] = new[] { "notepad", "блокнот" },
    };

    /// <summary>Optional user-supplied intents.json overriding the built-in taxonomy.</summary>
    public string CustomIntentsFile { get; set; } = "";
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
