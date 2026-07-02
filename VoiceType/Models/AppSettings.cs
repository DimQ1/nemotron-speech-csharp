using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // ── Sessions ────────────────────────────────────
    public bool SaveSessions { get; set; } = true;
    public string SessionsPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType", "Sessions");
    public bool SaveAudioMp3 { get; set; } = true;

    // ── Post-processing ─────────────────────────────
    public bool PostProcessingEnabled { get; set; } = true;
    public List<PostProcessingRule> PostProcessingRules { get; set; } = new()
    {
        new() { Name = "Remove language tags (<ru-RU>, <en>, <auto>, etc.)", Pattern = @"<(?:[a-z]{2}(-[A-Z]{1,3})?|auto)>", Replacement = "" },
    };

    // ── Hotkey ──────────────────────────────────────
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";

    // ── Downloader ───────────────────────────────────
    public string DownloaderRepoId { get; set; } = "";
    public string DownloaderModelsRootPath { get; set; } = "";
    public string DownloaderSelectedFoldersRepoId { get; set; } = "";
    public List<string> DownloaderSelectedFolders { get; set; } = new();
}

public enum InjectionMethod { InputSimulator, SendInput, Clipboard }

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
