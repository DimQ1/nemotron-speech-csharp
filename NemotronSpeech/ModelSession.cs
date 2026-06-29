using CommonUtils;
using Microsoft.ML.OnnxRuntimeGenAI;
using SpeechLib;
using System.Text.Json;

namespace NemotronSpeech;

/// <summary>
/// Nemotron ONNX Runtime GenAI speech recognizer.
/// Wraps model lifecycle: config, model, processor, tokenizer, generator.
/// Implements <see cref="IStreamingSpeechRecognizer"/> for pluggable recognition pipelines.
/// </summary>
public sealed class ModelSession : IStreamingSpeechRecognizer
{
    private readonly Config _config;
    private readonly Model _model;
    private readonly StreamingProcessor _processor;
    private readonly Tokenizer _tokenizer;
    private readonly TokenizerStream _tokenizerStream;
    private readonly GeneratorParams _genParams;
    private readonly Generator _generator;

    public int SampleRate { get; }
    public int ChunkSamples { get; }
    public string VadStatus { get; }
    public bool IsSingleLanguage { get; }

    public ModelSession(string modelPath, string executionProvider, string? langId, bool useVad)
    {
        modelPath = ResolvePath(modelPath);
        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"Model path not found: {modelPath}");

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelPath, "genai_config.json")));
        var cfg = json.RootElement.GetProperty("model");
        SampleRate = cfg.GetProperty("sample_rate").GetInt32();
        ChunkSamples = cfg.GetProperty("chunk_samples").GetInt32();

        // Detect single-language models: encoder has no lang_id input
        var encInputs = cfg.GetProperty("encoder").GetProperty("inputs");
        IsSingleLanguage = !encInputs.TryGetProperty("lang_id", out _);

        _config = Common.GetConfig(modelPath, executionProvider, null, new GeneratorParamsArgs());
        _model = new Model(_config);
        _processor = new StreamingProcessor(_model);

        _processor.SetOption("use_vad", "false");
        if (useVad)
            TrySetVad();

        VadStatus = _processor.GetOption("use_vad");

        _tokenizer = new Tokenizer(_model);
        _tokenizerStream = _tokenizer.CreateStream();
        _genParams = new GeneratorParams(_model);
        _generator = new Generator(_model, _genParams);

        if (!IsSingleLanguage && langId is not null)
            SetLanguage(langId);
    }

    public NamedTensors? ProcessAudio(float[] chunk) => _processor.Process(chunk);
    public NamedTensors? Flush() => _processor.Flush();
    public void SetInputs(NamedTensors inputs) => _generator.SetInputs(inputs);

    /// <inheritdoc />
    string? IStreamingSpeechRecognizer.ProcessAudio(float[] chunk)
    {
        var inputs = _processor.Process(chunk);
        if (inputs is null) return null;
        _generator.SetInputs(inputs);
        return DecodeTokens();
    }

    /// <inheritdoc />
    string? IStreamingSpeechRecognizer.Flush()
    {
        var inputs = _processor.Flush();
        if (inputs is null) return null;
        _generator.SetInputs(inputs);
        return DecodeTokens();
    }

    int IStreamingSpeechRecognizer.SampleRate => SampleRate;
    int IStreamingSpeechRecognizer.ChunkSamples => ChunkSamples;

    public string DecodeTokens()
    {
        var text = "";
        while (!_generator.IsDone())
        {
            _generator.GenerateNextToken();
            var tokens = _generator.GetNextTokens();
            if (tokens.Length > 0)
            {
                var t = _tokenizerStream.Decode(tokens[0]);
                if (!string.IsNullOrEmpty(t)) { Console.Write(t); text += t; }
            }
        }
        return text;
    }

    public void Dispose()
    {
        _generator.Dispose();
        _genParams.Dispose();
        _tokenizerStream.Dispose();
        _tokenizer.Dispose();
        _processor.Dispose();
        _model.Dispose();
        _config.Dispose();
    }

    private void SetLanguage(string langId)
    {
        try { _generator.SetRuntimeOption("lang_id", langId); }
        catch (Exception e) { Console.WriteLine($"  Warning: lang_id not set ({e.Message})"); }
    }

    private void TrySetVad()
    {
        try { _processor.SetOption("use_vad", "true"); }
        catch (Exception e) { Console.WriteLine($"  VAD: disabled ({e.Message})"); }
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", path));
}
