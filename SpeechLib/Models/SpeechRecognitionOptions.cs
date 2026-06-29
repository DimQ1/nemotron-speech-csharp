namespace SpeechLib.Models;

/// <summary>
/// Options for configuring a speech recognition session.
/// </summary>
public sealed record SpeechRecognitionOptions
{
    /// <summary>Path to the speech recognition model files.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>
    /// Execution provider / device target (e.g. "cpu", "cuda", "dml", "follow_config").
    /// </summary>
    public string ExecutionProvider { get; init; } = "follow_config";

    /// <summary>BCP-47 language code or numeric lang_id (null = auto-detect).</summary>
    public string? Language { get; init; }

    /// <summary>Enable Voice Activity Detection if supported by the engine.</summary>
    public bool UseVad { get; init; }
}
