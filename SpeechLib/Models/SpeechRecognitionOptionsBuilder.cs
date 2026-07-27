using System.IO;

namespace SpeechLib.Models;

/// <summary>
/// Builder for creating validated SpeechRecognitionOptions.
/// Provides fluent API with validation at Build() time.
/// </summary>
public sealed class SpeechRecognitionOptionsBuilder
{
    private string _modelPath = "";
    private string _executionProvider = "follow_config";
    private string? _language;
    private bool _useVad;

    public SpeechRecognitionOptionsBuilder WithModelPath(string modelPath)
    {
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        return this;
    }

    public SpeechRecognitionOptionsBuilder WithExecutionProvider(string executionProvider)
    {
        _executionProvider = executionProvider ?? throw new ArgumentNullException(nameof(executionProvider));
        return this;
    }

    public SpeechRecognitionOptionsBuilder WithLanguage(string? language)
    {
        _language = language;
        return this;
    }

    public SpeechRecognitionOptionsBuilder WithVad(bool useVad = true)
    {
        _useVad = useVad;
        return this;
    }

    /// <summary>
    /// Build and validate the options.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when model path is empty or does not exist.</exception>
    /// <exception cref="ArgumentException">Thrown when execution provider is invalid.</exception>
    public SpeechRecognitionOptions Build()
    {
        if (string.IsNullOrWhiteSpace(_modelPath))
            throw new ArgumentException("Model path cannot be empty.", nameof(_modelPath));

        if (!Directory.Exists(_modelPath))
            throw new ArgumentException($"Model path does not exist: {_modelPath}", nameof(_modelPath));

        var validProviders = new[] { "cpu", "cuda", "dml", "follow_config", "tensorrt", "NvTensorRtRtx" };
        if (!validProviders.Contains(_executionProvider, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Invalid execution provider: {_executionProvider}. Valid: {string.Join(", ", validProviders)}",
                nameof(_executionProvider));

        return new SpeechRecognitionOptions
        {
            ModelPath = _modelPath,
            ExecutionProvider = _executionProvider,
            Language = _language,
            UseVad = _useVad
        };
    }
}
