using System.Globalization;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services.Playback;

/// <summary>Where the playhead is in a transcript: segment and word indexes into the lists (−1 = none).</summary>
public readonly record struct PlaybackLocation(int SegmentIndex, int WordIndex)
{
    public static PlaybackLocation None { get; } = new(-1, -1);
}

/// <summary>
/// Pure playback helpers shared by the player bar and the transcript view (design §6.3):
/// position → current segment/word, previous/next segment, ±10 s skips, speed list, time labels.
/// Segments are expected in time order (as stored in <c>transcripts/vN.json</c>).
/// </summary>
public static class PlaybackCursor
{
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2.0;

    /// <summary>Speeds the speed button cycles through.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.75, 1.0, 1.25, 1.5, 2.0];

    /// <summary>The −10 s / +10 s skip.</summary>
    public static TimeSpan SkipInterval { get; } = TimeSpan.FromSeconds(10);

    /// <summary>"Previous" within this much of a segment start goes to the segment before it (media-player convention).</summary>
    public static TimeSpan PreviousRestartThreshold { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The segment being played at <paramref name="position"/>: the last non-empty segment that
    /// started at or before it. It stays current through the pause after it, so the highlight
    /// does not flicker between sentences. The word is the last one started at or before the
    /// position, while the position is still inside the segment (−1 in the pause after it).
    /// </summary>
    public static PlaybackLocation Locate(IReadOnlyList<TranscriptSegment> segments, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(segments);
        double t = position.TotalSeconds;
        int segment = LastStartedAtOrBefore(segments.Count, i => segments[i].Start, t);
        while (segment >= 0 && string.IsNullOrWhiteSpace(segments[segment].Text))
            segment--;
        if (segment < 0) return PlaybackLocation.None;

        var current = segments[segment];
        int word = -1;
        if (t < current.End && current.Words.Count > 0)
            word = LastStartedAtOrBefore(current.Words.Count, i => current.Words[i].Start, t);
        return new PlaybackLocation(segment, word);
    }

    /// <summary>Start of the segment "previous" goes to: the current segment's start, or the one before when already near it.</summary>
    public static TimeSpan PreviousSegmentStart(IReadOnlyList<TranscriptSegment> segments, TimeSpan position)
    {
        var location = Locate(segments, position);
        if (location.SegmentIndex < 0) return TimeSpan.Zero;
        var start = Seconds(segments[location.SegmentIndex].Start);
        if (position - start > PreviousRestartThreshold) return start;
        for (int i = location.SegmentIndex - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(segments[i].Text))
                return Seconds(segments[i].Start);
        }
        return TimeSpan.Zero;
    }

    /// <summary>Start of the first non-empty segment that starts after <paramref name="position"/>; null when none.</summary>
    public static TimeSpan? NextSegmentStart(IReadOnlyList<TranscriptSegment> segments, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(segments);
        int i = LastStartedAtOrBefore(segments.Count, k => segments[k].Start, position.TotalSeconds) + 1;
        for (; i < segments.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(segments[i].Text))
                return Seconds(segments[i].Start);
        }
        return null;
    }

    /// <summary><paramref name="position"/> + <paramref name="delta"/>, clamped to [0, <paramref name="duration"/>].</summary>
    public static TimeSpan Skip(TimeSpan position, TimeSpan delta, TimeSpan duration) =>
        Clamp(position + delta, duration);

    /// <summary>Clamps to [0, <paramref name="duration"/>].</summary>
    public static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position;
    }

    /// <summary>The speed after <paramref name="current"/> in <see cref="Speeds"/> (wraps; an unlisted speed goes to the next larger one).</summary>
    public static double NextSpeed(double current)
    {
        foreach (var speed in Speeds)
        {
            if (speed > current + 1e-6) return speed;
        }
        return Speeds[0];
    }

    /// <summary>"1×", "1.25×", "0.75×".</summary>
    public static string FormatSpeed(double speed) =>
        speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";

    /// <summary>"3:24", or "1:02:03" when <paramref name="total"/> is an hour or longer (so both labels have the same shape).</summary>
    public static string FormatTime(TimeSpan time, TimeSpan total)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        long seconds = (long)Math.Floor(time.TotalSeconds);
        return total.TotalHours >= 1 || seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>Segment start positions as fractions of <paramref name="duration"/> (seek bar ticks; the first segment at 0 is left out).</summary>
    public static IReadOnlyList<double> SegmentTicks(IReadOnlyList<TranscriptSegment> segments, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (duration <= TimeSpan.Zero) return [];
        var ticks = new List<double>(segments.Count);
        foreach (var s in segments)
        {
            double f = s.Start / duration.TotalSeconds;
            if (f > 0 && f < 1 && !string.IsNullOrWhiteSpace(s.Text)) ticks.Add(f);
        }
        return ticks;
    }

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds));

    /// <summary>Binary search: index of the last item with start ≤ t (−1 when none).</summary>
    private static int LastStartedAtOrBefore(int count, Func<int, double> start, double t)
    {
        int lo = 0, hi = count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (start(mid) <= t + 1e-9) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}
