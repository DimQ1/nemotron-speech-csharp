namespace SpeechLib.Models;

/// <summary>
/// A single utterance with speaker identity, time range, and transcribed text.
/// Produced by merging ASR word timestamps with diarization speaker segments.
/// </summary>
public sealed record DiarizedUtterance
{
    /// <summary>Speaker identifier (e.g. "SPEAKER_00").</summary>
    public string SpeakerId { get; init; } = "";

    /// <summary>Start time in seconds from the beginning of the recording.</summary>
    public double StartSeconds { get; init; }

    /// <summary>End time in seconds from the beginning of the recording.</summary>
    public double EndSeconds { get; init; }

    /// <summary>Transcribed text spoken by this speaker in this time range.</summary>
    public string Text { get; init; } = "";

    /// <summary>Duration in seconds.</summary>
    public double DurationSeconds => EndSeconds - StartSeconds;

    /// <summary>Formatted timestamp for display (e.g. "[00:03.2]").</summary>
    public string TimestampDisplay => FormatTimestamp(StartSeconds);

    /// <summary>Format seconds as [MM:SS.F].</summary>
    public static string FormatTimestamp(double seconds)
    {
        int min = (int)(seconds / 60);
        double sec = seconds % 60;
        return $"[{min:D2}:{sec:00.0}]";
    }

    /// <summary>Full display line: "[00:03.2] SPEAKER_00: text".</summary>
    public override string ToString() => $"{TimestampDisplay} {SpeakerId}: {Text}";
}
