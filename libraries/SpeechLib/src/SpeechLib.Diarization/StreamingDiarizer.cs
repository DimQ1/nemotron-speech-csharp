namespace SpeechLib.Diarization;

/// <summary>New results since the previous <see cref="IDiarizer.Feed"/> / <see cref="IDiarizer.Flush"/> call.</summary>
/// <param name="FirstFrame">Absolute index (10 ms frames from time zero) of the first row of <paramref name="Probabilities"/>.</param>
/// <param name="Probabilities">Newly finalized speaker-activity rows, row-major <c>frames x 8</c>. These frames never change afterwards.</param>
/// <param name="NewTurns">Turns that closed during this call (a turn closes once its speaker has been silent longer than the merge gap, or at the end of the stream), in closing order.</param>
/// <param name="FrontierSeconds">Everything before this time is final.</param>
public sealed record DiarizationUpdate(long FirstFrame, float[] Probabilities, IReadOnlyList<SpeakerTurn> NewTurns, double FrontierSeconds)
{
    /// <summary>Number of new 10 ms frames.</summary>
    public int FrameCount => Probabilities.Length / SpeakerActivityTimeline.Speakers;
}

/// <summary>Streaming speaker diarization: feed 16 kHz mono float PCM, receive 10 ms speaker probabilities and finalized turns.</summary>
public interface IDiarizer : IDisposable
{
    /// <summary>Latency preset in use.</summary>
    DiarizationPreset Preset { get; }

    /// <summary>Geometry of <see cref="Preset"/>.</summary>
    StreamingGeometry Geometry { get; }

    /// <summary>Audio buffered before a result for a given instant appears: (chunk + right context) x 80 ms. Excludes compute time.</summary>
    double BufferLatencySeconds { get; }

    /// <summary>Everything before this time is final (the diarization frontier).</summary>
    double FrontierSeconds { get; }

    /// <summary>The probability timeline produced so far; live, safe to read from another thread.</summary>
    SpeakerActivityTimeline Timeline { get; }

    /// <summary>
    /// Adds audio and runs every step that has enough look-ahead. Not reentrant: call from one thread at a time
    /// (concurrent calls are serialized). If <paramref name="ct"/> cancels, the diarizer stays consistent: steps that
    /// already ran are in <see cref="Timeline"/> and <see cref="GetTurns"/>, the rest run on the next call.
    /// </summary>
    DiarizationUpdate Feed(ReadOnlySpan<float> samples16k, CancellationToken ct = default);

    /// <summary>Ends the stream: scores the remaining audio and closes open turns. No further <see cref="Feed"/> afterwards.</summary>
    DiarizationUpdate Flush(CancellationToken ct = default);

    /// <summary>Snapshot of finalized turns so far, sorted by start time.</summary>
    IReadOnlyList<SpeakerTurn> GetTurns();

    /// <summary>Snapshot of turns still growing (provisional end), for live display.</summary>
    IReadOnlyList<SpeakerTurn> GetOpenTurns();
}

/// <summary>Options of a <see cref="StreamingDiarizer"/>.</summary>
public sealed class StreamingDiarizerOptions
{
    /// <summary>Latency preset. <see cref="DiarizationPreset.LowLatency"/> (1.04 s) is the desktop default, <see cref="DiarizationPreset.Eco2s"/> the phone one.</summary>
    public DiarizationPreset Preset { get; set; } = DiarizationPreset.LowLatency;

    /// <summary>Threshold, gap merging and minimum turn length.</summary>
    public PostProcessingOptions PostProcessing { get; set; } = PostProcessingOptions.Default;

    /// <summary>When set, probabilities older than this many seconds behind the frontier are dropped from the timeline. Null keeps everything (23 MB per two hours).</summary>
    public double? MaxRetainedSeconds { get; set; }
}

/// <summary>
/// Incremental Streaming Sortformer: log-mel front-end (ONNX), step graph (ONNX) and the speaker-cache host logic.
/// One instance per audio stream; instances may share one <see cref="DiarizationModel"/>.
/// Memory is bounded: audio and mel frames are kept only as far back as the next step needs.
/// </summary>
public sealed class StreamingDiarizer : IDiarizer
{
    private const int Hop = DiarizationConfig.HopSamples;
    private const int Mels = DiarizationConfig.MelBins;
    private const int Sub = DiarizationConfig.Subsampling;
    private const int Spk = DiarizationConfig.NumSpeakers;
    private const int Hidden = DiarizationConfig.HiddenSize;

    private readonly DiarizationModel _model;
    private readonly bool _ownsModel;
    private readonly StreamingGeometry _geo;
    private readonly AoscState _state;
    private readonly TurnBuilder _builder;
    private readonly float _preemph;
    private readonly long _retainFrames;
    private readonly object _sync = new();

    private readonly SpeakerActivityTimeline _timeline = new();
    private readonly List<SpeakerTurn> _turns = [];

    // Pre-emphasized, left-padded signal; padded coordinate p holds sample p - 256. Stored from _padBase.
    private float[] _pad = new float[1 << 16];
    private int _padLen;
    private long _padBase;
    private long _samples;
    private float _prev;

    // Mel frames [_melBase, _melBase + _melRows), row-major x 128.
    private float[] _mel = new float[Mels * 4096];
    private int _melRows;
    private long _melBase;

    private long _s;               // next step's first encoder frame
    private bool _flushed;
    private bool _disposed;

    // Per-call accumulators.
    private readonly List<float> _outProbs = [];
    private readonly List<SpeakerTurn> _outTurns = [];
    private long _outFirst;

    /// <summary>Creates a diarizer on a shared model. The model is not disposed with the diarizer.</summary>
    public StreamingDiarizer(DiarizationModel model, StreamingDiarizerOptions? options = null)
        : this(model, options, ownsModel: false)
    {
    }

    /// <summary>Loads a model and creates a diarizer that owns it.</summary>
    public static StreamingDiarizer Create(DiarizationModelOptions modelOptions, StreamingDiarizerOptions? options = null)
    {
        var model = DiarizationModel.Load(modelOptions);
        try { return new StreamingDiarizer(model, options, ownsModel: true); }
        catch { model.Dispose(); throw; }
    }

    private StreamingDiarizer(DiarizationModel model, StreamingDiarizerOptions? options, bool ownsModel)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new StreamingDiarizerOptions();
        _model = model;
        _ownsModel = ownsModel;
        Preset = options.Preset;
        _geo = model.Config.GetGeometry(Preset);
        _state = new AoscState(_geo.FifoLen, _geo.UpdatePeriod, model.Config.SilenceEmbed, model.Config.Aosc);
        _builder = new TurnBuilder(options.PostProcessing ?? PostProcessingOptions.Default);
        _preemph = model.Config.Preemphasis;
        _retainFrames = options.MaxRetainedSeconds is { } m
            ? Math.Max(100, (long)Math.Ceiling(m / SpeakerActivityTimeline.FrameSeconds))
            : long.MaxValue;
        _pad.AsSpan(0, DiarizationConfig.CenterPad).Clear();
        _padLen = DiarizationConfig.CenterPad;
    }

    /// <inheritdoc />
    public DiarizationPreset Preset { get; }

    /// <inheritdoc />
    public StreamingGeometry Geometry => _geo;

    /// <inheritdoc />
    public double BufferLatencySeconds => _geo.BufferLatencySeconds;

    /// <inheritdoc />
    public double FrontierSeconds => _timeline.FrontierSeconds;

    /// <inheritdoc />
    public SpeakerActivityTimeline Timeline => _timeline;

    /// <summary>Samples fed so far.</summary>
    public long SamplesFed
    {
        get { lock (_sync) return _samples; }
    }

    /// <summary>Pre-emphasized samples currently buffered (bounded: tests assert it does not grow with the stream).</summary>
    internal int BufferedSamples
    {
        get { lock (_sync) return _padLen; }
    }

    /// <summary>Mel frames currently buffered.</summary>
    internal int BufferedMelFrames
    {
        get { lock (_sync) return _melRows; }
    }

    /// <inheritdoc />
    public DiarizationUpdate Feed(ReadOnlySpan<float> samples16k, CancellationToken ct = default)
    {
        lock (_sync)
        {
            ThrowIfUnusable();
            AppendAudio(samples16k);
            BeginUpdate();
            ct.ThrowIfCancellationRequested();
            RunReadySteps(ct);
            return EndUpdate();
        }
    }

    /// <inheritdoc />
    public DiarizationUpdate Flush(CancellationToken ct = default)
    {
        lock (_sync)
        {
            ThrowIfUnusable();
            BeginUpdate();
            RunReadySteps(ct);
            RunFinalSteps(ct);
            _flushed = true;
            var closed = new List<SpeakerTurn>();
            _builder.Finish(closed);
            AddTurns(closed);
            return EndUpdate();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeakerTurn> GetTurns()
    {
        lock (_sync) return _turns.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<SpeakerTurn> GetOpenTurns()
    {
        lock (_sync)
        {
            var open = new List<SpeakerTurn>();
            _builder.SnapshotOpen(open);
            TurnBuilder.SortTurns(open);
            return open;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsModel) _model.Dispose();
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_flushed) throw new InvalidOperationException("The diarizer was flushed; create a new one for another stream.");
    }

    private void BeginUpdate()
    {
        _outProbs.Clear();
        _outTurns.Clear();
        _outFirst = _timeline.FrameCount;
    }

    private DiarizationUpdate EndUpdate()
    {
        if (_timeline.FrameCount - _retainFrames > _timeline.FirstFrame)
            _timeline.DiscardBefore(_timeline.FrameCount - _retainFrames);
        return new DiarizationUpdate(_outFirst, _outProbs.ToArray(), _outTurns.ToArray(), _timeline.FrontierSeconds);
    }

    // ---------------------------------------------------------------------------------- audio / mel

    private void AppendAudio(ReadOnlySpan<float> x)
    {
        if (x.IsEmpty) return;
        EnsurePadCapacity(_padLen + x.Length);
        int o = _padLen;
        float prev = _prev;
        int i = 0;
        if (_samples == 0)
        {
            _pad[o++] = x[0];
            prev = x[0];
            i = 1;
        }
        for (; i < x.Length; i++)
        {
            float v = x[i];
            _pad[o++] = v - _preemph * prev;
            prev = v;
        }
        _prev = prev;
        _samples += x.Length;
        _padLen = o;
    }

    private void EnsurePadCapacity(int needed)
    {
        if (needed <= _pad.Length) return;
        Array.Resize(ref _pad, Math.Max(needed, _pad.Length * 2));
    }

    private long PadEnd => _padBase + _padLen;

    /// <summary>Computes mel frames up to (excluding) <paramref name="upTo"/>; samples past the end of the audio read as zeros.</summary>
    private void EnsureMel(long upTo)
    {
        long from = _melBase + _melRows;
        if (upTo <= from) return;
        long start = from * Hop;
        int len = (int)((upTo - 1) * Hop + DiarizationConfig.NFft - start);
        var seg = new float[len];
        long avail = Math.Min(PadEnd, start + len) - start;
        if (avail > 0) Array.Copy(_pad, (int)(start - _padBase), seg, 0, (int)avail);
        var mel = _model.RunFrontend(seg);
        int rows = (int)(upTo - from);
        if (mel.Length != rows * Mels)
            throw new InvalidOperationException($"Front-end returned {mel.Length / (float)Mels} frames, expected {rows}.");
        int need = (_melRows + rows) * Mels;
        if (need > _mel.Length) Array.Resize(ref _mel, Math.Max(need, _mel.Length * 2));
        mel.CopyTo(_mel, _melRows * Mels);
        _melRows += rows;
    }

    /// <summary>Drops audio and mel frames the next step no longer reads.</summary>
    private void TrimBuffers()
    {
        long nextMel = _s * Sub;
        int dropRows = (int)Math.Min(Math.Max(nextMel - _melBase, 0), _melRows);
        if (dropRows > 0)
        {
            Array.Copy(_mel, dropRows * Mels, _mel, 0, (_melRows - dropRows) * Mels);
            _melRows -= dropRows;
            _melBase += dropRows;
        }
        long nextPad = nextMel * Hop;
        int dropPad = (int)Math.Min(Math.Max(nextPad - _padBase, 0), _padLen);
        if (dropPad > 0)
        {
            Array.Copy(_pad, dropPad, _pad, 0, _padLen - dropPad);
            _padLen -= dropPad;
            _padBase += dropPad;
        }
    }

    // ---------------------------------------------------------------------------------- schedule

    private void RunReadySteps(CancellationToken ct)
    {
        int chunk = _geo.ChunkLen;
        long f = (long)(chunk + _geo.RightContext) * Sub;
        while (true)
        {
            long ms = _s * Sub;
            // The HF processor's rule: a chunk runs once the audio covers its last mel frame in full.
            long need = ms * Hop - DiarizationConfig.CenterPad + f * Hop + 400;
            if (_samples < need) break;
            ct.ThrowIfCancellationRequested();
            RunChunk(_s, ms + f, chunk, chunk * Sub);
            _s += chunk;
            TrimBuffers();
        }
    }

    private void RunFinalSteps(CancellationToken ct)
    {
        long tValid = _samples / Hop;
        int chunk = _geo.ChunkLen;
        if (Preset == DiarizationPreset.Offline)
        {
            // HF offline: T + 1 frames (the last one zeroed), chunks of 340 with a truncated look-ahead at the end.
            long ne = (tValid + 1 + Sub - 1) / Sub;
            while (_s < ne && _s * Sub < tValid)
            {
                ct.ThrowIfCancellationRequested();
                long e = Math.Min(_s + chunk, ne);
                long ceEnd = Math.Min(e + _geo.RightContext, ne);
                int emit = (int)(Math.Min(e * Sub, tValid) - _s * Sub);
                RunChunk(_s, ceEnd * Sub, (int)(e - _s), emit);
                _s = e;
                TrimBuffers();
            }
        }
        else if (_s * Sub < tValid)
        {
            // Streaming: one last chunk without look-ahead; every frame is scored.
            ct.ThrowIfCancellationRequested();
            int nIn = (int)((tValid - _s * Sub + Sub - 1) / Sub);
            RunChunk(_s, tValid, nIn, (int)(tValid - _s * Sub));
            _s += nIn;
            TrimBuffers();
        }
    }

    /// <param name="s">First encoder frame of the chunk.</param>
    /// <param name="melEnd">Exclusive mel frame end of the step input (chunk + look-ahead); frames at or past the real end are zeros.</param>
    /// <param name="nChunk">Encoder frames that enter the FIFO (the chunk without look-ahead).</param>
    /// <param name="emit">10 ms output frames to publish.</param>
    private void RunChunk(long s, long melEnd, int nChunk, int emit)
    {
        long ms = s * Sub;
        long tValid = _samples / Hop;
        EnsureMel(Math.Min(melEnd, tValid));

        int nMel = (int)(melEnd - ms);
        int nIn = (nMel + Sub - 1) / Sub;
        var feats = new float[nIn * Sub * Mels];
        long realEnd = Math.Min(melEnd, _melBase + _melRows);
        int rows = (int)(realEnd - ms);
        Array.Copy(_mel, (int)(ms - _melBase) * Mels, feats, 0, rows * Mels);

        int nc = _state.CachedFrames;
        var cached = new float[nc * Hidden];
        _state.CopyCached(cached);
        var step = _model.RunStep(feats, nIn * Sub, cached, nc);

        _state.Update(step.ChunkEmbeds.AsSpan(0, nChunk * Hidden), step.Probs80);

        var probs = step.Probs10.AsSpan(nc * Sub * Spk, emit * Spk);
        _timeline.Append(probs);
        _outProbs.AddRange(probs);
        var closed = new List<SpeakerTurn>();
        _builder.Push(probs, closed);
        AddTurns(closed);
    }

    private void AddTurns(List<SpeakerTurn> closed)
    {
        foreach (var t in closed)
        {
            _outTurns.Add(t);
            int idx = _turns.BinarySearch(t, TurnComparer.Instance);
            _turns.Insert(idx < 0 ? ~idx : idx, t);
        }
    }

    private sealed class TurnComparer : IComparer<SpeakerTurn>
    {
        public static readonly TurnComparer Instance = new();
        public int Compare(SpeakerTurn a, SpeakerTurn b) =>
            a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Speaker.CompareTo(b.Speaker);
    }
}
