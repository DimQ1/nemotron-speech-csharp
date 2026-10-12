namespace SpeechLib.Diarization;

/// <summary>
/// Speaker-activity probabilities at 10 ms resolution, 8 channels per frame, stored in fixed-size pages so a
/// two-hour recording (720k frames, 23 MB) never needs one large allocation. One writer appends; any number of
/// readers may query concurrently (all members take an internal lock). Frame <c>f</c> covers
/// <c>[f * 0.01 s, (f + 1) * 0.01 s)</c>.
/// </summary>
public sealed class SpeakerActivityTimeline
{
    /// <summary>Speaker channels per frame.</summary>
    public const int Speakers = DiarizationConfig.NumSpeakers;
    /// <summary>Seconds per frame.</summary>
    public const double FrameSeconds = DiarizationConfig.OutputFrameSeconds;

    private const int PageFrames = 2048;

    private readonly object _lock = new();
    private readonly List<float[]> _pages = [];
    private long _pageBase;       // absolute index of _pages[0]
    private long _frameCount;     // absolute end frame (exclusive)

    /// <summary>First frame still stored; frames below it were discarded by <see cref="DiscardBefore"/>.</summary>
    public long FirstFrame
    {
        get { lock (_lock) return _pageBase * PageFrames; }
    }

    /// <summary>Frames produced so far, counted from time zero (the diarization frontier).</summary>
    public long FrameCount
    {
        get { lock (_lock) return _frameCount; }
    }

    /// <summary>Time covered so far, in seconds: everything before it is final.</summary>
    public double FrontierSeconds => FrameCount * FrameSeconds;

    /// <summary>Builds a timeline from a row-major <c>frames x 8</c> matrix.</summary>
    public static SpeakerActivityTimeline FromMatrix(ReadOnlySpan<float> probabilities)
    {
        if (probabilities.Length % Speakers != 0)
            throw new ArgumentException($"Length {probabilities.Length} is not a multiple of {Speakers}.", nameof(probabilities));
        var t = new SpeakerActivityTimeline();
        t.Append(probabilities);
        return t;
    }

    /// <summary>Appends whole frames (row-major, 8 floats each).</summary>
    public void Append(ReadOnlySpan<float> rows)
    {
        if (rows.Length % Speakers != 0)
            throw new ArgumentException($"Length {rows.Length} is not a multiple of {Speakers}.", nameof(rows));
        lock (_lock)
        {
            while (!rows.IsEmpty)
            {
                long inPage = _frameCount % PageFrames;
                if (inPage == 0 && _frameCount / PageFrames - _pageBase == _pages.Count)
                    _pages.Add(new float[PageFrames * Speakers]);
                var page = _pages[(int)(_frameCount / PageFrames - _pageBase)];
                int take = Math.Min(rows.Length / Speakers, PageFrames - (int)inPage);
                rows[..(take * Speakers)].CopyTo(page.AsSpan((int)inPage * Speakers));
                rows = rows[(take * Speakers)..];
                _frameCount += take;
            }
        }
    }

    /// <summary>Drops whole pages that end at or before <paramref name="frame"/> (bounded memory for live use).</summary>
    public void DiscardBefore(long frame)
    {
        lock (_lock)
        {
            long pagesToDrop = Math.Min(frame / PageFrames - _pageBase, _pages.Count);
            // never drop the page that is still being filled
            if (_frameCount % PageFrames != 0 && _pageBase + pagesToDrop > (_frameCount - 1) / PageFrames)
                pagesToDrop = (_frameCount - 1) / PageFrames - _pageBase;
            if (pagesToDrop <= 0) return;
            _pages.RemoveRange(0, (int)pagesToDrop);
            _pageBase += pagesToDrop;
        }
    }

    /// <summary>Probability of <paramref name="speaker"/> at <paramref name="frame"/>; 0 outside the stored range.</summary>
    public float Get(long frame, int speaker)
    {
        lock (_lock)
        {
            if ((uint)speaker >= Speakers || frame >= _frameCount || frame < _pageBase * PageFrames) return 0f;
            return _pages[(int)(frame / PageFrames - _pageBase)][(int)(frame % PageFrames) * Speakers + speaker];
        }
    }

    /// <summary>
    /// Mean probability per speaker over frames <c>[from, to)</c>, clipped to the stored range, written into
    /// <paramref name="mean"/> (8 floats). Returns the number of frames averaged (0 leaves <paramref name="mean"/> zeroed).
    /// </summary>
    public int Average(long from, long to, Span<float> mean)
    {
        if (mean.Length < Speakers) throw new ArgumentException("mean needs 8 slots.", nameof(mean));
        mean[..Speakers].Clear();
        Span<double> sum = stackalloc double[Speakers];
        int n = 0;
        lock (_lock)
        {
            from = Math.Max(from, _pageBase * PageFrames);
            to = Math.Min(to, _frameCount);
            for (long f = from; f < to; f++)
            {
                var page = _pages[(int)(f / PageFrames - _pageBase)];
                int o = (int)(f % PageFrames) * Speakers;
                for (int k = 0; k < Speakers; k++) sum[k] += page[o + k];
                n++;
            }
        }
        if (n == 0) return 0;
        for (int k = 0; k < Speakers; k++) mean[k] = (float)(sum[k] / n);
        return n;
    }

    /// <summary>Copies frames <c>[from, to)</c> (clipped to the stored range) into a new row-major matrix.</summary>
    public float[] CopyRange(long from, long to)
    {
        lock (_lock)
        {
            from = Math.Max(from, _pageBase * PageFrames);
            to = Math.Min(to, _frameCount);
            if (to <= from) return [];
            var result = new float[(to - from) * Speakers];
            for (long f = from; f < to; f++)
            {
                var page = _pages[(int)(f / PageFrames - _pageBase)];
                Array.Copy(page, (int)(f % PageFrames) * Speakers, result, (f - from) * Speakers, Speakers);
            }
            return result;
        }
    }

    /// <summary>All stored frames as a row-major matrix.</summary>
    public float[] ToMatrix() => CopyRange(FirstFrame, FrameCount);
}
