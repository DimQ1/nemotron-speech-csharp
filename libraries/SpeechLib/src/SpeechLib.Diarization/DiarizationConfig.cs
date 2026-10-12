using System.Text.Json;

namespace SpeechLib.Diarization;

/// <summary>Latency presets. Geometry comes from <c>diar_config.json</c>; one ONNX graph serves all of them.</summary>
public enum DiarizationPreset
{
    /// <summary>Model-card offline geometry: 30.4 s buffer, 27.2 s hop. Cheapest per audio second, most accurate.</summary>
    Offline,
    /// <summary>1.04 s buffer, FIFO 264. The model card's default streaming preset.</summary>
    LowLatency,
    /// <summary>0.64 s buffer.</summary>
    VeryLowLatency,
    /// <summary>0.32 s buffer. About 3x the cost of <see cref="LowLatency"/> per audio second, no accuracy gain.</summary>
    UltraLowLatency,
    /// <summary>1.12 s buffer, FIFO 80 (NeMo-Speech.cpp "v3-streaming"). About half the cost of <see cref="LowLatency"/>.</summary>
    NcppV3Streaming,
    /// <summary>2.08 s buffer, FIFO 48. The cheapest live preset, intended for phones.</summary>
    Eco2s,
}

/// <summary>Streaming geometry in 80 ms encoder frames.</summary>
/// <param name="ChunkLen">Frames emitted per step.</param>
/// <param name="RightContext">Look-ahead frames re-encoded with every step.</param>
/// <param name="FifoLen">FIFO capacity before frames move into the speaker cache.</param>
/// <param name="UpdatePeriod">Minimum number of frames popped from the FIFO per cache update.</param>
public readonly record struct StreamingGeometry(int ChunkLen, int RightContext, int FifoLen, int UpdatePeriod)
{
    /// <summary>Seconds per encoder frame (8 mel frames of 10 ms).</summary>
    public const double FrameSeconds = 0.08;

    /// <summary>Audio that must be buffered before a step can run: (chunk + right) x 80 ms. Excludes compute time.</summary>
    public double BufferLatencySeconds => (ChunkLen + RightContext) * FrameSeconds;

    /// <summary>Audio advanced per step: chunk x 80 ms.</summary>
    public double HopSeconds => ChunkLen * FrameSeconds;
}

/// <summary>AOSC (arrival-order speaker cache) hyper-parameters.</summary>
public sealed record AoscParameters(
    int CacheLen,
    int SilenceFramesPerSpeaker,
    float PredScoreThreshold,
    float LatestFramesBoost,
    float StrongBoostRate,
    float WeakBoostRate,
    float MinPosScoresRate);

/// <summary>Thrown for missing or inconsistent model files and malformed configuration.</summary>
public sealed class DiarizationModelException : Exception
{
    public DiarizationModelException(string message) : base(message) { }
    public DiarizationModelException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Parsed and validated <c>diar_config.json</c>.</summary>
public sealed class DiarizationConfig
{
    /// <summary>Speaker channels of the model.</summary>
    public const int NumSpeakers = 8;
    /// <summary>Encoder hidden size, i.e. width of the cache embeddings.</summary>
    public const int HiddenSize = 512;
    /// <summary>Mel frames per encoder frame.</summary>
    public const int Subsampling = 8;
    /// <summary>Mel bins.</summary>
    public const int MelBins = 128;
    /// <summary>Mel hop in samples (10 ms at 16 kHz).</summary>
    public const int HopSamples = 160;
    /// <summary>Left zero padding of the pre-emphasized signal, in samples (centered STFT).</summary>
    public const int CenterPad = 256;
    /// <summary>STFT window length in samples; a frame reads <c>NFft</c> samples.</summary>
    public const int NFft = 512;
    /// <summary>Required input sample rate.</summary>
    public const int SampleRate = 16000;
    /// <summary>Seconds per 10 ms output frame.</summary>
    public const double OutputFrameSeconds = 0.01;

    private readonly Dictionary<DiarizationPreset, StreamingGeometry> _geometries;

    private DiarizationConfig(Dictionary<DiarizationPreset, StreamingGeometry> geometries, AoscParameters aosc,
        float[] silenceEmbed, float preemphasis)
    {
        _geometries = geometries;
        Aosc = aosc;
        SilenceEmbed = silenceEmbed;
        Preemphasis = preemphasis;
    }

    /// <summary>AOSC parameters.</summary>
    public AoscParameters Aosc { get; }

    /// <summary>The learned 512-d embedding that fills the speaker cache's silence slots.</summary>
    public float[] SilenceEmbed { get; }

    /// <summary>Pre-emphasis coefficient (0.97).</summary>
    public float Preemphasis { get; }

    /// <summary>Presets present in the file.</summary>
    public IReadOnlyCollection<DiarizationPreset> Presets => _geometries.Keys;

    /// <summary>Geometry of <paramref name="preset"/>.</summary>
    public StreamingGeometry GetGeometry(DiarizationPreset preset) =>
        _geometries.TryGetValue(preset, out var g)
            ? g
            : throw new DiarizationModelException($"diar_config.json has no geometry for preset '{ConfigKey(preset)}'.");

    /// <summary>Key of a preset in the config (<c>offline</c> block or <c>modes</c> entry).</summary>
    public static string ConfigKey(DiarizationPreset preset) => preset switch
    {
        DiarizationPreset.Offline => "offline",
        DiarizationPreset.LowLatency => "low_latency",
        DiarizationPreset.VeryLowLatency => "very_low_latency",
        DiarizationPreset.UltraLowLatency => "ultra_low_latency",
        DiarizationPreset.NcppV3Streaming => "ncpp_v3_streaming",
        DiarizationPreset.Eco2s => "eco_2s",
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    /// <summary>Loads and validates a config file.</summary>
    public static DiarizationConfig Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DiarizationModelException($"Cannot read diarization config '{path}': {e.Message}", e);
        }
        return Parse(json, path);
    }

    /// <summary>Parses and validates config JSON. <paramref name="source"/> is used in error messages only.</summary>
    public static DiarizationConfig Parse(string json, string source = "diar_config.json")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseCore(doc.RootElement, source);
        }
        catch (JsonException e)
        {
            throw new DiarizationModelException($"{source} is not valid JSON: {e.Message}", e);
        }
    }

    private static DiarizationConfig ParseCore(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new DiarizationModelException($"{source}: root must be a JSON object.");

        Expect(root, "sample_rate", SampleRate, source);
        Expect(root, "num_speakers", NumSpeakers, source);
        Expect(root, "hidden_size", HiddenSize, source);
        Expect(root, "subsampling_factor", Subsampling, source);

        var fe = Obj(root, "frontend", source);
        Expect(fe, "n_fft", NFft, source + " frontend");
        Expect(fe, "win_length", 400, source + " frontend");
        Expect(fe, "hop_length", HopSamples, source + " frontend");
        Expect(fe, "n_mels", MelBins, source + " frontend");
        Expect(fe, "center_pad", CenterPad, source + " frontend");
        float preemph = fe.TryGetProperty("preemphasis", out var pe) && pe.TryGetSingle(out var pf) ? pf : 0.97f;
        if (preemph is <= 0f or >= 1f)
            throw new DiarizationModelException($"{source}: frontend.preemphasis {preemph} is out of range (0, 1).");

        var a = Obj(root, "aosc", source);
        var aosc = new AoscParameters(
            Int(a, "spkcache_len", source), Int(a, "sil_frames_per_spk", source),
            Float(a, "pred_score_threshold", source), Float(a, "latest_frames_score_boost", source),
            Float(a, "strong_boost_rate", source), Float(a, "weak_boost_rate", source),
            Float(a, "min_pos_scores_rate", source));
        if (aosc.CacheLen <= 0 || aosc.CacheLen % NumSpeakers != 0)
            throw new DiarizationModelException($"{source}: aosc.spkcache_len {aosc.CacheLen} must be a positive multiple of {NumSpeakers}.");
        if (aosc.SilenceFramesPerSpeaker < 0 || aosc.CacheLen / NumSpeakers - aosc.SilenceFramesPerSpeaker <= 0)
            throw new DiarizationModelException($"{source}: aosc.sil_frames_per_spk {aosc.SilenceFramesPerSpeaker} leaves no frames per speaker.");

        if (!root.TryGetProperty("silence_embed", out var se) || se.ValueKind != JsonValueKind.Array)
            throw new DiarizationModelException($"{source}: missing 'silence_embed' array.");
        var silence = new float[HiddenSize];
        int n = 0;
        foreach (var e in se.EnumerateArray())
        {
            if (n >= HiddenSize) { n++; break; }
            if (!e.TryGetSingle(out silence[n]))
                throw new DiarizationModelException($"{source}: silence_embed[{n}] is not a number.");
            n++;
        }
        if (n != HiddenSize)
            throw new DiarizationModelException($"{source}: silence_embed has {(n > HiddenSize ? "more than " + HiddenSize : n.ToString())} values, expected {HiddenSize}.");

        var geos = new Dictionary<DiarizationPreset, StreamingGeometry>();
        var offline = Obj(root, "offline", source);
        geos[DiarizationPreset.Offline] = Geometry(offline, "offline", aosc, source);
        var modes = Obj(root, "modes", source);
        foreach (var preset in Enum.GetValues<DiarizationPreset>())
        {
            if (preset == DiarizationPreset.Offline) continue;
            string key = ConfigKey(preset);
            if (modes.TryGetProperty(key, out var m))
                geos[preset] = Geometry(m, key, aosc, source);
        }
        return new DiarizationConfig(geos, aosc, silence, preemph);
    }

    private static StreamingGeometry Geometry(JsonElement e, string name, AoscParameters aosc, string source)
    {
        var g = new StreamingGeometry(Int(e, "chunk_len", source + " " + name), Int(e, "right_context", source + " " + name),
            Int(e, "fifo_len", source + " " + name), Int(e, "update_period", source + " " + name));
        if (g.ChunkLen <= 0 || g.RightContext < 0 || g.FifoLen < 0 || g.UpdatePeriod <= 0)
            throw new DiarizationModelException($"{source}: geometry '{name}' has invalid values ({g}).");
        return g;
    }

    private static JsonElement Obj(JsonElement e, string name, string source) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? v
            : throw new DiarizationModelException($"{source}: missing object '{name}'.");

    private static int Int(JsonElement e, string name, string source) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i)
            ? i
            : throw new DiarizationModelException($"{source}: missing or non-integer '{name}'.");

    private static float Float(JsonElement e, string name, string source) =>
        e.TryGetProperty(name, out var v) && v.TryGetSingle(out float f)
            ? f
            : throw new DiarizationModelException($"{source}: missing or non-numeric '{name}'.");

    private static void Expect(JsonElement e, string name, int expected, string source)
    {
        int actual = Int(e, name, source);
        if (actual != expected)
            throw new DiarizationModelException(
                $"{source}: '{name}' is {actual}, this build of SpeechLib.Diarization supports {expected} only.");
    }
}
