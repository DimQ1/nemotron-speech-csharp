using System;
using System.IO;
using System.Text;
using System.Text.Json;
using CommonUtils;
using Microsoft.ML.OnnxRuntimeGenAI;
using SpeechLib;

namespace NemotronSpeech;

/// <summary>
/// Holds a loaded ASR model (Model + Tokenizer) once in memory.
/// Creates lightweight per-job <see cref="IStreamingSpeechRecognizer"/> sessions
/// that share the underlying model. Each session owns its own encoder/decoder state
/// (StreamingProcessor, Generator, TokenizerStream) which are NOT thread-safe.
/// </summary>
public sealed class SharedModelHost : IDisposable
{
    private readonly Config _config;
    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly bool _isSingleLanguage;
    private readonly string? _langId;
    private readonly bool _useVad;
    private readonly GeneratorParamsArgs _searchOptions;
    private readonly int _sampleRate;
    private readonly int _chunkSamples;
    private bool _isDisposed;

    public int SampleRate => _sampleRate;
    public int ChunkSamples => _chunkSamples;

    public SharedModelHost(string modelPath, string executionProvider, string? langId, bool useVad,
        GeneratorParamsArgs? searchOptions = null)
    {
        modelPath = ResolvePath(modelPath);
        if (!Directory.Exists(modelPath))
            throw new DirectoryNotFoundException($"Model path not found: {modelPath}");

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelPath, "genai_config.json")));
        var cfg = json.RootElement.GetProperty("model");
        _sampleRate = cfg.GetProperty("sample_rate").GetInt32();
        _chunkSamples = cfg.GetProperty("chunk_samples").GetInt32();

        var encInputs = cfg.GetProperty("encoder").GetProperty("inputs");
        _isSingleLanguage = !encInputs.TryGetProperty("lang_id", out _);

        _searchOptions = searchOptions ?? new GeneratorParamsArgs
        {
            num_beams = string.Equals(executionProvider, "cpu", StringComparison.OrdinalIgnoreCase) ? 1 : 4,
            do_sample = false,
            repetition_penalty = 1.1
        };

        _config = Common.GetConfig(modelPath, executionProvider, null, _searchOptions);
        _model = new Model(_config);
        _tokenizer = new Tokenizer(_model);
        _langId = langId;
        _useVad = useVad;
    }

    /// <summary>Create a lightweight session that shares the model but has its own encoder/decoder state.</summary>
    public IStreamingSpeechRecognizer CreateSession()
    {
        var session = new BatchSession(this);
        return session;
    }

    /// <summary>Lightweight session: shares Model+Tokenizer, owns Processor+Generator+Stream.</summary>
    private sealed class BatchSession : IStreamingSpeechRecognizer, IDisposable
    {
        private readonly SharedModelHost _host;
        private readonly StreamingProcessor _processor;
        private readonly GeneratorParams _genParams;
        private readonly Generator _generator;
        private readonly TokenizerStream _tokenizerStream;
        private int _lastTokenCount;
        private bool _isDisposed;

        public int SampleRate => _host._sampleRate;
        public int ChunkSamples => _host._chunkSamples;
        public int LastTokenCount => _lastTokenCount;

        public BatchSession(SharedModelHost host)
        {
            _host = host;
            _processor = new StreamingProcessor(host._model);
            _processor.SetOption("use_vad", "false");
            if (host._useVad)
            {
                try { _processor.SetOption("use_vad", "true"); }
                catch (Exception e) { Console.WriteLine($"  VAD: disabled ({e.Message})"); }
            }

            _genParams = new GeneratorParams(host._model);
            Common.SetSearchOptions(_genParams, host._searchOptions, verbose: false);
            _generator = new Generator(host._model, _genParams);
            _tokenizerStream = host._tokenizer.CreateStream();

            if (!host._isSingleLanguage && host._langId is not null)
            {
                try { _generator.SetRuntimeOption("lang_id", host._langId); }
                catch (Exception e) { Console.WriteLine($"  Warning: lang_id not set ({e.Message})"); }
            }
        }

        string? IStreamingSpeechRecognizer.ProcessAudio(float[] chunk)
        {
            var inputs = _processor.Process(chunk);
            if (inputs is null) return null;
            _generator.SetInputs(inputs);
            return DecodeTokens();
        }

        string? IStreamingSpeechRecognizer.Flush()
        {
            var inputs = _processor.Flush();
            if (inputs is null) return null;
            _generator.SetInputs(inputs);
            return DecodeTokens();
        }

        int IStreamingSpeechRecognizer.SampleRate => SampleRate;
        int IStreamingSpeechRecognizer.ChunkSamples => ChunkSamples;

        private string DecodeTokens()
        {
            var text = new StringBuilder();
            int tokenCount = 0;
            while (!_generator.IsDone())
            {
                _generator.GenerateNextToken();
                var tokens = _generator.GetNextTokens();
                if (tokens.Length > 0)
                {
                    tokenCount++;
                    var t = _tokenizerStream.Decode(tokens[0]);
                    if (!string.IsNullOrEmpty(t))
                        text.Append(t);
                }
            }
            _lastTokenCount = tokenCount;
            return text.ToString();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _generator.Dispose();
            _genParams.Dispose();
            _tokenizerStream.Dispose();
            _processor.Dispose();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _tokenizer.Dispose();
        _model.Dispose();
        _config.Dispose();
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", path));
}
