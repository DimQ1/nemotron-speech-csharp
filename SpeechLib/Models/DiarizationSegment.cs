namespace SpeechLib.Models;

/// <summary>
/// A diarization segment — a time range assigned to a specific speaker,
/// optionally containing transcribed text.
/// </summary>
public sealed record DiarizationSegment
{
    /// <summary>Speaker identifier (e.g. "SPEAKER_00").</summary>
    public string SpeakerId { get; init; } = "";

    /// <summary>Start time in seconds.</summary>
    public double StartSeconds { get; init; }

    /// <summary>End time in seconds.</summary>
    public double EndSeconds { get; init; }

    /// <summary>Transcribed text for this segment (populated after ASR merge).</summary>
    public string Text { get; init; } = "";
}
