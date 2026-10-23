using System.Text.Json;

namespace SpeechLib.Nemotron;

/// <summary>
/// Typed view of the <c>genai_config.json</c> of a Nemotron streaming ASR model folder. The file keeps
/// its GenAI name because existing model folders carry it, but it is read as plain JSON: this class
/// does not need ONNX Runtime GenAI.
/// </summary>
/// <remarks>
/// <para>Defaults of absent members and the number handling follow the upstream GenAI 0.16.0 config reader, so a
/// folder that loads there parses to the same values here (for example an absent <c>vad.threshold</c> is 0.5, not the
/// 0.3 the shipped folders spell out). Unknown members are ignored.</para>
/// <para>The <c>vad</c> block is read as leniently as upstream reads it: an empty or absent <c>filename</c> means
/// "no VAD model" (upstream: VAD off; see <see cref="NemotronVadConfig.HasModel"/>), <c>threshold</c> is not range
/// checked (the Silero gate clamps it to [0, 1] like upstream) and a negative duration counts as one chunk. The
/// sample rate is not restricted here either; a model that is not 16 kHz simply cannot use the native Silero gate, which
/// says so when it is asked for. <see cref="Validate"/> only rejects what the streaming pipeline cannot work with at all
/// (a missing size, an inconsistent geometry), which upstream leaves to fail later inside the model.</para>
/// </remarks>
public sealed record NemotronModelConfig
{
    /// <summary>
    /// Config file that marks a folder as a Nemotron model folder. The name is inherited from the ONNX Runtime GenAI
    /// export format the models are published in; the product no longer uses GenAI.
    /// </summary>
    public const string ConfigFileName = "genai_config.json";

    /// <summary>Value of <c>model.type</c> for the Nemotron streaming transducer.</summary>
    public const string ModelTypeName = "nemotron_speech";

    public string ModelType { get; init; } = "";
    public int VocabSize { get; init; }
    public int NumMels { get; init; }
    public int FftSize { get; init; }
    public int HopLength { get; init; }
    public int WinLength { get; init; }
    public float Preemph { get; init; }
    public float LogEps { get; init; }
    public int SubsamplingFactor { get; init; }
    public int LeftContext { get; init; }
    public int ConvContext { get; init; }
    public int PreEncodeCacheSize { get; init; }
    public int SampleRate { get; init; }
    public int ChunkSamples { get; init; }
    public int BlankId { get; init; }
    public int MaxSymbolsPerStep { get; init; }

    public NemotronEncoderConfig Encoder { get; init; } = new();
    public NemotronDecoderConfig Decoder { get; init; } = new();
    public NemotronJoinerConfig Joiner { get; init; } = new();

    /// <summary>The <c>model.vad</c> block, or null when the folder has none.</summary>
    public NemotronVadConfig? Vad { get; init; }

    /// <summary><c>search.blank_penalty</c>: subtracted from the blank logit before the argmax (default 0).</summary>
    public float BlankPenalty { get; init; }

    /// <summary>Mel frames the front-end produces for one chunk.</summary>
    public int MelFramesPerChunk => ChunkSamples / HopLength;

    /// <summary>Encoder output frames per chunk implied by the config (the graph is authoritative).</summary>
    public int EncoderFramesPerChunk => MelFramesPerChunk / SubsamplingFactor;

    /// <summary>Audio samples covered by one encoder frame (1280 = 80 ms at 16 kHz).</summary>
    public int SamplesPerEncoderFrame => HopLength * SubsamplingFactor;

    /// <summary>Rows of the encoder input: the cached mel frames plus the new ones.</summary>
    public int EncoderInputFrames => PreEncodeCacheSize + MelFramesPerChunk;

    /// <summary>Reads and validates <c>genai_config.json</c> of a model folder.</summary>
    public static NemotronModelConfig Load(string modelDirectory)
    {
        var path = Path.Combine(modelDirectory, ConfigFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{ConfigFileName} not found in {modelDirectory}", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses and validates the JSON text of a <c>genai_config.json</c>.</summary>
    /// <exception cref="InvalidDataException">The JSON is not a valid Nemotron streaming config.</exception>
    public static NemotronModelConfig Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{ConfigFileName} is not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{ConfigFileName} has no \"model\" object.");

            var config = new NemotronModelConfig
            {
                ModelType = ReadString(model, "type", ""),
                VocabSize = ReadInt(model, "vocab_size"),
                NumMels = ReadInt(model, "num_mels"),
                FftSize = ReadInt(model, "fft_size"),
                HopLength = ReadInt(model, "hop_length"),
                WinLength = ReadInt(model, "win_length"),
                Preemph = ReadFloat(model, "preemph"),
                LogEps = ReadFloat(model, "log_eps"),
                SubsamplingFactor = ReadInt(model, "subsampling_factor"),
                LeftContext = ReadInt(model, "left_context"),
                ConvContext = ReadInt(model, "conv_context"),
                PreEncodeCacheSize = ReadInt(model, "pre_encode_cache_size"),
                SampleRate = ReadInt(model, "sample_rate"),
                ChunkSamples = ReadInt(model, "chunk_samples"),
                BlankId = ReadInt(model, "blank_id"),
                MaxSymbolsPerStep = ReadInt(model, "max_symbols_per_step"),
                Encoder = ParseEncoder(Section(model, "encoder")),
                Decoder = ParseDecoder(Section(model, "decoder")),
                Joiner = ParseJoiner(Section(model, "joiner")),
                Vad = Section(model, "vad") is { } vad ? ParseVad(vad) : null,
                BlankPenalty = Section(root, "search") is { } search ? ReadFloat(search, "blank_penalty") : 0f,
            };
            config.Validate();
            return config;
        }
    }

    /// <summary>Checks the numbers the streaming pipeline relies on.</summary>
    /// <exception cref="InvalidDataException">A value is missing, out of range or inconsistent.</exception>
    public void Validate()
    {
        if (!string.Equals(ModelType, ModelTypeName, StringComparison.Ordinal))
            throw Invalid($"model.type is \"{ModelType}\", expected \"{ModelTypeName}\"");

        RequirePositive(VocabSize, "model.vocab_size");
        RequirePositive(NumMels, "model.num_mels");
        RequirePositive(FftSize, "model.fft_size");
        RequirePositive(HopLength, "model.hop_length");
        RequirePositive(WinLength, "model.win_length");
        RequirePositive(SubsamplingFactor, "model.subsampling_factor");
        RequirePositive(LeftContext, "model.left_context");
        RequirePositive(ConvContext, "model.conv_context");
        RequirePositive(PreEncodeCacheSize, "model.pre_encode_cache_size");
        RequirePositive(SampleRate, "model.sample_rate");
        RequirePositive(ChunkSamples, "model.chunk_samples");
        RequirePositive(MaxSymbolsPerStep, "model.max_symbols_per_step");
        RequirePositive(Encoder.HiddenSize, "model.encoder.hidden_size");
        RequirePositive(Encoder.NumLayers, "model.encoder.num_hidden_layers");
        RequirePositive(Decoder.HiddenSize, "model.decoder.hidden_size");
        RequirePositive(Decoder.NumLayers, "model.decoder.num_hidden_layers");

        if (!(Preemph >= 0f && Preemph < 1f))
            throw Invalid($"model.preemph must be in [0, 1), got {Preemph}");
        if (!(LogEps > 0f))
            throw Invalid($"model.log_eps must be positive, got {LogEps}");
        if (WinLength > FftSize)
            throw Invalid($"model.win_length ({WinLength}) exceeds model.fft_size ({FftSize})");
        if ((FftSize & (FftSize - 1)) != 0)
            throw Invalid($"model.fft_size ({FftSize}) must be a power of two");
        if (ChunkSamples % HopLength != 0)
            throw Invalid($"model.chunk_samples ({ChunkSamples}) must be a multiple of model.hop_length ({HopLength})");
        if (MelFramesPerChunk % SubsamplingFactor != 0)
            throw Invalid($"a chunk has {MelFramesPerChunk} mel frames, not a multiple of model.subsampling_factor ({SubsamplingFactor})");
        if (BlankId < 0 || BlankId >= VocabSize)
            throw Invalid($"model.blank_id ({BlankId}) is outside the vocabulary (0..{VocabSize - 1})");
        if (float.IsNaN(BlankPenalty) || float.IsInfinity(BlankPenalty))
            throw Invalid("search.blank_penalty is not a finite number");

        // The vad block is deliberately not validated: see the remarks of this class.
    }

    /// <summary>
    /// True when <paramref name="modelPath"/> is a Nemotron streaming model folder: a
    /// <c>genai_config.json</c> whose <c>model.type</c> is <c>nemotron_speech</c> and the encoder, decoder
    /// and joint graphs it names are present.
    /// </summary>
    public static bool IsNemotronModel(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            return false;
        try
        {
            var configPath = Path.Combine(modelPath, ConfigFileName);
            if (!File.Exists(configPath))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.Object
                || !string.Equals(ReadString(model, "type", ""), ModelTypeName, StringComparison.Ordinal))
                return false;

            return File.Exists(Path.Combine(modelPath, ReadString(Section(model, "encoder"), "filename", NemotronEncoderConfig.DefaultFileName)))
                && File.Exists(Path.Combine(modelPath, ReadString(Section(model, "decoder"), "filename", NemotronDecoderConfig.DefaultFileName)))
                && File.Exists(Path.Combine(modelPath, ReadString(Section(model, "joiner"), "filename", NemotronJoinerConfig.DefaultFileName)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    // ── parsing helpers ─────────────────────────────────────────────────

    private static NemotronEncoderConfig ParseEncoder(JsonElement? e)
    {
        var inputs = Section(e, "inputs");
        var outputs = Section(e, "outputs");
        return new NemotronEncoderConfig
        {
            FileName = ReadString(e, "filename", NemotronEncoderConfig.DefaultFileName),
            HiddenSize = ReadInt(e, "hidden_size"),
            NumLayers = ReadInt(e, "num_hidden_layers"),
            AudioInput = ReadString(inputs, "audio_features", "audio_signal"),
            LengthInput = ReadString(inputs, "input_lengths", "length"),
            CacheChannelInput = ReadString(inputs, "cache_last_channel", "cache_last_channel"),
            CacheTimeInput = ReadString(inputs, "cache_last_time", "cache_last_time"),
            CacheChannelLengthInput = ReadString(inputs, "cache_last_channel_len", "cache_last_channel_len"),
            LanguageInput = ReadString(inputs, "lang_id", ""),
            EncodedOutput = ReadString(outputs, "encoder_outputs", "outputs"),
            LengthOutput = ReadString(outputs, "output_lengths", "encoded_lengths"),
            CacheChannelOutput = ReadString(outputs, "cache_last_channel_next", "cache_last_channel_next"),
            CacheTimeOutput = ReadString(outputs, "cache_last_time_next", "cache_last_time_next"),
            CacheChannelLengthOutput = ReadString(outputs, "cache_last_channel_len_next", "cache_last_channel_len_next"),
        };
    }

    private static NemotronDecoderConfig ParseDecoder(JsonElement? e)
    {
        var inputs = Section(e, "inputs");
        var outputs = Section(e, "outputs");
        return new NemotronDecoderConfig
        {
            FileName = ReadString(e, "filename", NemotronDecoderConfig.DefaultFileName),
            HiddenSize = ReadInt(e, "hidden_size"),
            NumLayers = ReadInt(e, "num_hidden_layers"),
            TargetsInput = ReadString(inputs, "targets", "targets"),
            HiddenStateInput = ReadString(inputs, "lstm_hidden_state", "h_in"),
            CellStateInput = ReadString(inputs, "lstm_cell_state", "c_in"),
            Output = ReadString(outputs, "outputs", "decoder_output"),
            HiddenStateOutput = ReadString(outputs, "lstm_hidden_state", "h_out"),
            CellStateOutput = ReadString(outputs, "lstm_cell_state", "c_out"),
        };
    }

    private static NemotronJoinerConfig ParseJoiner(JsonElement? e)
    {
        var inputs = Section(e, "inputs");
        var outputs = Section(e, "outputs");
        return new NemotronJoinerConfig
        {
            FileName = ReadString(e, "filename", NemotronJoinerConfig.DefaultFileName),
            EncoderInput = ReadString(inputs, "encoder_outputs", "encoder_output"),
            DecoderInput = ReadString(inputs, "decoder_outputs", "decoder_output"),
            Output = ReadString(outputs, "logits", "joint_output"),
        };
    }

    private static NemotronVadConfig ParseVad(JsonElement e) => new()
    {
        FileName = ReadString(e, "filename", ""),
        Threshold = ReadFloat(e, "threshold", 0.5f),
        SilenceDurationMs = ReadInt(e, "silence_duration_ms", 500),
        PrefixPaddingMs = ReadInt(e, "prefix_padding_ms", 300),
    };

    private static JsonElement? Section(JsonElement? parent, string name)
    {
        if (parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
            return value;
        return null;
    }

    private static string ReadString(JsonElement? parent, string name, string fallback)
    {
        if (parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? fallback;
        return fallback;
    }

    private static int ReadInt(JsonElement? parent, string name, int fallback = 0)
    {
        if (parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            // GenAI reads every number as a double and truncates it with a range check.
            var d = value.GetDouble();
            if (d >= int.MinValue && d <= int.MaxValue)
                return (int)d;
            throw Invalid($"{name} is out of range: {d}");
        }
        return fallback;
    }

    private static float ReadFloat(JsonElement? parent, string name, float fallback = 0f)
    {
        if (parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            return (float)value.GetDouble(); // double first, like GenAI's static_cast<float>(double)
        return fallback;
    }

    private static void RequirePositive(int value, string name)
    {
        if (value <= 0)
            throw Invalid($"{name} must be positive, got {value}");
    }

    private static InvalidDataException Invalid(string message) => new($"{ConfigFileName}: {message}");
}

/// <summary>Encoder section: graph file, sizes and the names of its inputs and outputs.</summary>
public sealed record NemotronEncoderConfig
{
    public const string DefaultFileName = "encoder.onnx";

    public string FileName { get; init; } = DefaultFileName;
    public int HiddenSize { get; init; }
    public int NumLayers { get; init; }

    public string AudioInput { get; init; } = "audio_signal";
    public string LengthInput { get; init; } = "length";
    public string CacheChannelInput { get; init; } = "cache_last_channel";
    public string CacheTimeInput { get; init; } = "cache_last_time";
    public string CacheChannelLengthInput { get; init; } = "cache_last_channel_len";

    /// <summary>Name of the language-id input; empty for a single-language graph.</summary>
    public string LanguageInput { get; init; } = "";

    public string EncodedOutput { get; init; } = "outputs";
    public string LengthOutput { get; init; } = "encoded_lengths";
    public string CacheChannelOutput { get; init; } = "cache_last_channel_next";
    public string CacheTimeOutput { get; init; } = "cache_last_time_next";
    public string CacheChannelLengthOutput { get; init; } = "cache_last_channel_len_next";
}

/// <summary>Prediction-network (two-layer LSTM) section.</summary>
public sealed record NemotronDecoderConfig
{
    public const string DefaultFileName = "decoder.onnx";

    public string FileName { get; init; } = DefaultFileName;
    public int HiddenSize { get; init; }
    public int NumLayers { get; init; }

    public string TargetsInput { get; init; } = "targets";
    public string HiddenStateInput { get; init; } = "h_in";
    public string CellStateInput { get; init; } = "c_in";
    public string Output { get; init; } = "decoder_output";
    public string HiddenStateOutput { get; init; } = "h_out";
    public string CellStateOutput { get; init; } = "c_out";
}

/// <summary>Joint-network section.</summary>
public sealed record NemotronJoinerConfig
{
    /// <summary>Upstream default; the shipped folders name the file explicitly (<c>joint.onnx</c>).</summary>
    public const string DefaultFileName = "joiner.onnx";

    public string FileName { get; init; } = DefaultFileName;
    public string EncoderInput { get; init; } = "encoder_output";
    public string DecoderInput { get; init; } = "decoder_output";
    public string Output { get; init; } = "joint_output";
}

/// <summary>The <c>model.vad</c> block that drives the silence drop rule.</summary>
public sealed record NemotronVadConfig
{
    /// <summary>The Silero graph in the model folder; empty when the block names none.</summary>
    public string FileName { get; init; } = "";

    /// <summary>
    /// True when the block names a VAD model. A block without a file name is how upstream spells "VAD off": the
    /// durations stay readable (an external VAD filter uses them) but there is no model for the native gate.
    /// </summary>
    public bool HasModel => !string.IsNullOrWhiteSpace(FileName);

    /// <summary>Speech probability at or above which a 512-sample window counts as speech.</summary>
    public float Threshold { get; init; } = 0.5f;

    public int SilenceDurationMs { get; init; } = 500;
    public int PrefixPaddingMs { get; init; } = 300;
}
