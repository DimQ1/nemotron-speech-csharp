using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;

namespace SpeechLib.Diarization;

/// <summary>Output of an offline run.</summary>
/// <param name="Turns">Turns sorted by start time.</param>
/// <param name="Timeline">The 10 ms probabilities (null when <see cref="OfflineDiarizerOptions.KeepProbabilities"/> is off). Re-assign words or change the threshold from it without running the model again.</param>
/// <param name="DurationSeconds">Audio length.</param>
/// <param name="ElapsedSeconds">Wall-clock time of the run.</param>
/// <param name="Preset">Geometry used.</param>
public sealed record DiarizationResult(
    IReadOnlyList<SpeakerTurn> Turns,
    SpeakerActivityTimeline? Timeline,
    double DurationSeconds,
    double ElapsedSeconds,
    DiarizationPreset Preset)
{
    /// <summary>Distinct speaker channels that have at least one turn.</summary>
    public int SpeakerCount => Turns.Select(t => t.Speaker).Distinct().Count();

    /// <summary>Real-time factor: compute seconds per audio second.</summary>
    public double RealTimeFactor => DurationSeconds > 0 ? ElapsedSeconds / DurationSeconds : 0;

    /// <summary>Total seconds with at least one speaker, counting overlaps once per speaker.</summary>
    public double SpeechSeconds => Turns.Sum(t => t.Duration);
}

/// <summary>Options of an <see cref="OfflineDiarizer"/>.</summary>
public sealed class OfflineDiarizerOptions
{
    /// <summary>Geometry. Offline (30.4 s buffer) is the cheapest and most accurate; a streaming preset gives the "live" result.</summary>
    public DiarizationPreset Preset { get; set; } = DiarizationPreset.Offline;

    /// <summary>Threshold, gap merging and minimum turn length.</summary>
    public PostProcessingOptions PostProcessing { get; set; } = PostProcessingOptions.Default;

    /// <summary>Keep the 10 ms probabilities in the result (8 floats per frame, 23 MB per two hours).</summary>
    public bool KeepProbabilities { get; set; } = true;

    /// <summary>Audio fed per call, in seconds. Only affects progress granularity and cancellation latency.</summary>
    public double BlockSeconds { get; set; } = 5;
}

/// <summary>
/// Diarizes a whole recording by running the streaming engine over it block by block. Memory does not depend on the
/// recording length except for the optional probability timeline (and the audio itself when passed as an array: use
/// <see cref="DiarizeWavFile"/> or the block overload for long recordings).
/// </summary>
public sealed class OfflineDiarizer : IDisposable
{
    private readonly DiarizationModel _model;
    private readonly bool _ownsModel;
    private readonly OfflineDiarizerOptions _options;

    /// <summary>Creates a diarizer on a shared model. The model is not disposed with it.</summary>
    public OfflineDiarizer(DiarizationModel model, OfflineDiarizerOptions? options = null)
        : this(model, options, false)
    {
    }

    /// <summary>Loads a model and creates a diarizer that owns it.</summary>
    public static OfflineDiarizer Create(DiarizationModelOptions modelOptions, OfflineDiarizerOptions? options = null)
    {
        var model = DiarizationModel.Load(modelOptions);
        return new OfflineDiarizer(model, options, true);
    }

    private OfflineDiarizer(DiarizationModel model, OfflineDiarizerOptions? options, bool ownsModel)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _ownsModel = ownsModel;
        _options = options ?? new OfflineDiarizerOptions();
        if (_options.BlockSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(options), "BlockSeconds must be positive.");
    }

    /// <summary>Diarizes 16 kHz mono float samples. <paramref name="progress"/> receives 0..1.</summary>
    public DiarizationResult Diarize(ReadOnlyMemory<float> audio16k, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int block = BlockSamples();
        return Run(Blocks(audio16k, block), audio16k.Length / (double)DiarizationConfig.SampleRate, progress, ct);
    }

    /// <summary>Diarizes audio that arrives as blocks (any block size). <paramref name="totalSeconds"/> only drives progress.</summary>
    public DiarizationResult Diarize(IEnumerable<ReadOnlyMemory<float>> blocks, double? totalSeconds = null,
        IProgress<double>? progress = null, CancellationToken ct = default) =>
        Run(blocks, totalSeconds, progress, ct);

    /// <summary>
    /// Diarizes a 16 kHz mono WAV (PCM 16/24/32-bit or float32; multi-channel files are averaged) by streaming it from disk.
    /// </summary>
    public DiarizationResult DiarizeWavFile(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var wav = WavStream.Open(path);
        return Run(wav.ReadBlocks(BlockSamples()), wav.DurationSeconds, progress, ct);
    }

    /// <summary>Runs <see cref="Diarize(ReadOnlyMemory{float}, IProgress{double}?, CancellationToken)"/> on a worker thread.</summary>
    public Task<DiarizationResult> DiarizeAsync(ReadOnlyMemory<float> audio16k, IProgress<double>? progress = null,
        CancellationToken ct = default) =>
        Task.Run(() => Diarize(audio16k, progress, ct), ct);

    private int BlockSamples() => Math.Max(1600, (int)(_options.BlockSeconds * DiarizationConfig.SampleRate));

    private static IEnumerable<ReadOnlyMemory<float>> Blocks(ReadOnlyMemory<float> audio, int block)
    {
        for (int i = 0; i < audio.Length; i += block)
            yield return audio.Slice(i, Math.Min(block, audio.Length - i));
    }

    private DiarizationResult Run(IEnumerable<ReadOnlyMemory<float>> blocks, double? totalSeconds,
        IProgress<double>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var stream = new StreamingDiarizer(_model, new StreamingDiarizerOptions
        {
            Preset = _options.Preset,
            PostProcessing = _options.PostProcessing,
        });
        long samples = 0;
        foreach (var b in blocks)
        {
            ct.ThrowIfCancellationRequested();
            stream.Feed(b.Span, ct);
            samples += b.Length;
            if (totalSeconds is > 0)
                progress?.Report(Math.Min(0.99, samples / (double)DiarizationConfig.SampleRate / totalSeconds.Value));
        }
        stream.Flush(ct);
        progress?.Report(1.0);

        var turns = stream.GetTurns();
        var timeline = _options.KeepProbabilities ? stream.Timeline : null;
        return new DiarizationResult(turns, timeline, samples / (double)DiarizationConfig.SampleRate,
            sw.Elapsed.TotalSeconds, _options.Preset);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsModel) _model.Dispose();
    }
}

/// <summary>Minimal streaming reader for 16 kHz WAV files (PCM 16/24/32 or float32), mono or averaged to mono.</summary>
internal sealed class WavStream : IDisposable
{
    private readonly FileStream _fs;
    private readonly int _channels;
    private readonly int _bits;
    private readonly bool _isFloat;
    private readonly long _dataStart;
    private readonly long _dataBytes;

    private WavStream(FileStream fs, int channels, int bits, bool isFloat, long dataStart, long dataBytes)
    {
        _fs = fs; _channels = channels; _bits = bits; _isFloat = isFloat; _dataStart = dataStart; _dataBytes = dataBytes;
    }

    public double DurationSeconds => _dataBytes / (double)(_channels * (_bits / 8)) / DiarizationConfig.SampleRate;

    public static WavStream Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        try
        {
            Span<byte> hdr = stackalloc byte[12];
            fs.ReadExactly(hdr);
            if (!hdr[..4].SequenceEqual("RIFF"u8) || !hdr[8..12].SequenceEqual("WAVE"u8))
                throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");
            int channels = 1, bits = 16, rate = 0, format = 1;
            Span<byte> ch = stackalloc byte[8];
            while (fs.Read(ch) == 8)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(ch[4..]);
                if (ch[..4].SequenceEqual("fmt "u8))
                {
                    var f = new byte[size];
                    fs.ReadExactly(f);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(f);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(2));
                    rate = BinaryPrimitives.ReadInt32LittleEndian(f.AsSpan(4));
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(14));
                    if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(f.AsSpan(24));
                    if ((size & 1) != 0) fs.ReadByte();
                }
                else if (ch[..4].SequenceEqual("data"u8))
                {
                    if (rate != DiarizationConfig.SampleRate)
                        throw new NotSupportedException($"'{path}' is {rate} Hz; the diarizer needs 16000 Hz mono.");
                    bool isFloat = format == 3;
                    if (!(isFloat && bits == 32) && !(format == 1 && bits is 16 or 24 or 32))
                        throw new NotSupportedException($"'{path}': unsupported WAV format {format} / {bits} bit.");
                    long bytes = size < 0 || fs.Position + size > fs.Length ? fs.Length - fs.Position : size;
                    return new WavStream(fs, channels, bits, isFloat, fs.Position, bytes);
                }
                else
                {
                    fs.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
            throw new InvalidDataException($"'{path}' has no data chunk.");
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public IEnumerable<ReadOnlyMemory<float>> ReadBlocks(int blockSamples)
    {
        int frameBytes = _channels * (_bits / 8);
        var raw = ArrayPool<byte>.Shared.Rent(blockSamples * frameBytes);
        var block = new float[blockSamples];
        try
        {
            _fs.Position = _dataStart;
            long remaining = _dataBytes / frameBytes;
            while (remaining > 0)
            {
                int n = (int)Math.Min(blockSamples, remaining);
                _fs.ReadExactly(raw, 0, n * frameBytes);
                for (int i = 0; i < n; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < _channels; c++)
                        sum += Sample(raw, (i * _channels + c) * (_bits / 8));
                    block[i] = sum / _channels;
                }
                remaining -= n;
                yield return block.AsMemory(0, n);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(raw); }
    }

    private float Sample(byte[] b, int o) => _bits switch
    {
        16 => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o)) / 32768f,
        24 => (((b[o] << 8) | (b[o + 1] << 16) | (b[o + 2] << 24)) >> 8) / 8388608f,
        _ => _isFloat ? BitConverter.ToSingle(b, o) : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) / 2147483648f,
    };

    public void Dispose() => _fs.Dispose();
}
