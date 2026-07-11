namespace SpeechLib.Models;

/// <summary>
/// A single word with its start and end time in the audio stream.
/// </summary>
public sealed record WordTiming
{
    /// <summary>The word text.</summary>
    public string Word { get; init; } = "";

    /// <summary>Start time of the word in seconds.</summary>
    public double StartSeconds { get; init; }

    /// <summary>End time of the word in seconds.</summary>
    public double EndSeconds { get; init; }
}
