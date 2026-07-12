using System.IO;
namespace VoiceType.Models;

/// <summary>
/// A single speech recognition session with metadata and saved audio.
/// </summary>
public sealed class RecognitionSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime EndedAt { get; set; }
    public TimeSpan Duration => EndedAt - StartedAt;

    /// <summary>Safe filename prefix: start—end time + unique id.</summary>
    public string FileNameBase =>
        $"{StartedAt:yyyy-MM-dd_HH-mm-ss}__{EndedAt:yyyy-MM-dd_HH-mm-ss}__{Id}";

    public string Language { get; init; } = "";
    public string EngineProvider { get; init; } = "Nemotron";
    public string AudioSource { get; init; } = "Mic";

    public string RecognizedText { get; set; } = "";

    public string? AudioFilePath { get; set; }

    public bool IsComplete { get; set; }
}
