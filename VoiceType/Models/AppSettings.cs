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
    public string ModelPath { get; set; } = "";
    public string ExecutionProvider { get; set; } = "cpu";
    public string Language { get; set; } = "ru";
    public bool UseVad { get; set; } = true;

    // ── Capture ─────────────────────────────────────
    public string AudioSource { get; set; } = "Mic"; // Mic, Loopback, Mix

    // ── Injection ───────────────────────────────────
    public InjectionMethod TextInjectionMethod { get; set; } = InjectionMethod.SendInput;
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
        new() { Name = "Strip repeated punctuation", Pattern = @"([,.!?;:])\1+", Replacement = "$1" },
        new() { Name = "Remove 'uh', 'um' hesitation markers", Pattern = @"\b(uh+|um+|er+)\b", Replacement = "" },
        new() { Name = "Normalise whitespace", Pattern = @"\s{2,}", Replacement = " " },
    };

    // ── Hotkey ──────────────────────────────────────
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";
}

public enum InjectionMethod { SendInput, Clipboard }

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
