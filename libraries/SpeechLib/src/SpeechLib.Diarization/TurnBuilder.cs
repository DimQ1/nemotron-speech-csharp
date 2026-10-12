namespace SpeechLib.Diarization;

/// <summary>One continuous stretch of speech by one speaker.</summary>
/// <param name="Start">Start in seconds.</param>
/// <param name="End">End in seconds (exclusive).</param>
/// <param name="Speaker">Model channel 0..7. The model orders channels by first appearance, so numbers are
/// arrival order and stable within one run; they are not identities and differ between runs.</param>
/// <param name="OverlapSeconds">Seconds of this turn during which another speaker was active too.</param>
public readonly record struct SpeakerTurn(double Start, double End, int Speaker, double OverlapSeconds = 0)
{
    /// <summary>Length in seconds.</summary>
    public double Duration => End - Start;
    /// <summary>True when another speaker was active during at least a tenth of the turn.</summary>
    public bool HasOverlap => Duration > 0 && OverlapSeconds >= 0.1 * Duration;
}

/// <summary>Turns the per-frame probabilities into <see cref="SpeakerTurn"/>s.</summary>
public sealed record PostProcessingOptions
{
    /// <summary>A speaker is active in a frame when its probability is at least this. The model card and the spike use 0.5.</summary>
    public float Threshold { get; init; } = 0.5f;

    /// <summary>Turns shorter than this are dropped (after gap merging), in seconds.</summary>
    public double MinTurnSeconds { get; init; } = 0.25;

    /// <summary>Gaps of at most this many seconds inside one speaker's speech are bridged into a single turn.</summary>
    public double MergeGapSeconds { get; init; } = 0.2;

    /// <summary>Defaults for display: threshold 0.5, 0.25 s minimum turn, 0.2 s gap merging.</summary>
    public static PostProcessingOptions Default { get; } = new();

    /// <summary>Raw thresholding at 0.5 with no filtering: what the spike's DER table was produced with.</summary>
    public static PostProcessingOptions Raw { get; } = new() { MinTurnSeconds = 0, MergeGapSeconds = 0 };

    internal void Validate()
    {
        if (Threshold is <= 0f or >= 1f) throw new ArgumentOutOfRangeException(nameof(Threshold), "Threshold must be in (0, 1).");
        if (MinTurnSeconds < 0) throw new ArgumentOutOfRangeException(nameof(MinTurnSeconds));
        if (MergeGapSeconds < 0) throw new ArgumentOutOfRangeException(nameof(MergeGapSeconds));
    }
}

/// <summary>
/// Incremental run builder: pushes 10 ms frames, closes a speaker's run once the gap since its last active frame
/// exceeds the merge gap (so a closed turn can no longer grow), drops runs shorter than the minimum, and counts
/// overlap frames. Pure and single-threaded.
/// </summary>
internal sealed class TurnBuilder
{
    private const int N = SpeakerActivityTimeline.Speakers;
    private readonly float _threshold;
    private readonly int _minFrames;
    private readonly int _mergeGapFrames;

    private readonly bool[] _open = new bool[N];
    private readonly long[] _start = new long[N];
    private readonly long[] _lastActive = new long[N];
    private readonly long[] _overlap = new long[N];
    private long _frame;

    public TurnBuilder(PostProcessingOptions options)
    {
        options.Validate();
        _threshold = options.Threshold;
        _minFrames = (int)Math.Ceiling(options.MinTurnSeconds / SpeakerActivityTimeline.FrameSeconds - 1e-9);
        _mergeGapFrames = (int)Math.Round(options.MergeGapSeconds / SpeakerActivityTimeline.FrameSeconds);
    }

    /// <summary>Frames pushed so far.</summary>
    public long FrameCount => _frame;

    /// <summary>Pushes <c>rows.Length / 8</c> frames, appending closed turns to <paramref name="closed"/> in closing order.</summary>
    public void Push(ReadOnlySpan<float> rows, List<SpeakerTurn> closed)
    {
        for (int i = 0; i + N <= rows.Length; i += N)
        {
            var row = rows.Slice(i, N);
            int active = 0;
            for (int k = 0; k < N; k++) if (row[k] >= _threshold) active++;
            for (int k = 0; k < N; k++)
            {
                if (row[k] >= _threshold)
                {
                    if (!_open[k])
                    {
                        _open[k] = true;
                        _start[k] = _frame;
                        _overlap[k] = 0;
                    }
                    _lastActive[k] = _frame;
                    if (active >= 2) _overlap[k]++;
                }
                else if (_open[k] && _frame - _lastActive[k] > _mergeGapFrames)
                {
                    // inactive frames since the last active one (this frame included) exceed the bridgeable gap
                    Close(k, closed);
                }
            }
            _frame++;
        }
    }

    /// <summary>Closes every open run (end of stream).</summary>
    public void Finish(List<SpeakerTurn> closed)
    {
        for (int k = 0; k < N; k++)
            if (_open[k]) Close(k, closed);
    }

    /// <summary>Runs that are still growing, as provisional turns that end at their last active frame.</summary>
    public void SnapshotOpen(List<SpeakerTurn> open)
    {
        for (int k = 0; k < N; k++)
            if (_open[k] && _lastActive[k] + 1 - _start[k] >= _minFrames)
                open.Add(Make(k));
    }

    private void Close(int k, List<SpeakerTurn> closed)
    {
        _open[k] = false;
        if (_lastActive[k] + 1 - _start[k] >= _minFrames) closed.Add(Make(k));
    }

    private SpeakerTurn Make(int k) => new(
        _start[k] * SpeakerActivityTimeline.FrameSeconds,
        (_lastActive[k] + 1) * SpeakerActivityTimeline.FrameSeconds,
        k,
        _overlap[k] * SpeakerActivityTimeline.FrameSeconds);

    /// <summary>One-shot post-processing of a whole probability matrix, sorted by start time then speaker.</summary>
    public static List<SpeakerTurn> Process(ReadOnlySpan<float> rows, PostProcessingOptions options)
    {
        var b = new TurnBuilder(options);
        var turns = new List<SpeakerTurn>();
        b.Push(rows, turns);
        b.Finish(turns);
        SortTurns(turns);
        return turns;
    }

    internal static void SortTurns(List<SpeakerTurn> turns) =>
        turns.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Speaker.CompareTo(b.Speaker));
}
