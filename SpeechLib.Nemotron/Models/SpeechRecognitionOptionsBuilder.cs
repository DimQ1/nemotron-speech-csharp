namespace SpeechLib.Nemotron.Models;

/// <summary>Builds validated options for a Nemotron recognition session.</summary>
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

    /// <summary>Builds and validates the Nemotron options.</summary>
    /// <exception cref="ArgumentException">Thrown when a required option is empty or missing.</exception>
    public SpeechRecognitionOptions Build()
    {
        if (string.IsNullOrWhiteSpace(_modelPath))
            throw new ArgumentException("Model path cannot be empty.", nameof(_modelPath));

        if (!Directory.Exists(_modelPath))
            throw new ArgumentException($"Model path does not exist: {_modelPath}", nameof(_modelPath));

        if (string.IsNullOrWhiteSpace(_executionProvider))
            throw new ArgumentException("Execution provider cannot be empty.", nameof(_executionProvider));

        return new SpeechRecognitionOptions
        {
            ModelPath = _modelPath,
            ExecutionProvider = _executionProvider,
            Language = _language,
            UseVad = _useVad
        };
    }
}