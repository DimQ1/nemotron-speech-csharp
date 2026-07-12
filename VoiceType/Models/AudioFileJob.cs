using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using SpeechLib.Models;

namespace VoiceType.Models;

/// <summary>
/// Represents a single audio file queued for batch transcription.
/// Tracks file metadata, processing status, and result data.
/// </summary>
public sealed class AudioFileJob : INotifyPropertyChanged
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);

    private string _durationDisplay = "";
    public string DurationDisplay
    {
        get => _durationDisplay;
        set { _durationDisplay = value; OnPropertyChanged(); }
    }

    public double DurationSeconds { get; set; }

    private string _status = "Queued";
    /// <summary>Queued, Processing, Done, Error</summary>
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusIcon)); }
    }

    private int _progressPercent;
    public int ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(); }
    }

    /// <summary>Unicode icon: ⏳ Queued, 🔄 Processing, ✅ Done, ❌ Error</summary>
    public string StatusIcon => Status switch
    {
        "Queued" => "⏳",
        "Processing" => "🔄",
        "Done" => "✅",
        "Error" => "❌",
        _ => "⏳"
    };

    // ── Results (populated after successful processing) ──

    public string? PlainText { get; set; }
    public string? DiarizedText { get; set; }
    public List<WordTiming>? WordTimings { get; set; }
    public List<DiarizedUtterance>? SpeakerUtterances { get; set; }
    public string? OutputPath { get; set; }
    public string? ErrorMessage { get; set; }

    // ── INotifyPropertyChanged ──────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
