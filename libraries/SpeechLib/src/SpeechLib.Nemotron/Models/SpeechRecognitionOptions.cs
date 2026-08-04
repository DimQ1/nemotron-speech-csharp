namespace SpeechLib.Nemotron.Models;

/// <summary>Options for configuring a Nemotron recognition session.</summary>
public sealed record SpeechRecognitionOptions
{
    /// <summary>Path to the Nemotron model files.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>ONNX Runtime execution provider or device target.</summary>
    public string ExecutionProvider { get; init; } = "follow_config";

    /// <summary>BCP-47 language code or numeric lang_id (null = auto-detect).</summary>
    public string? Language { get; init; }

    /// <summary>Enable Voice Activity Detection.</summary>
    public bool UseVad { get; init; }
}