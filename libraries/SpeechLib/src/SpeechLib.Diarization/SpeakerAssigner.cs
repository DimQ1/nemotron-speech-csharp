using SpeechLib.Models;

namespace SpeechLib.Diarization;

/// <summary>Speaker label of one word.</summary>
/// <param name="Speaker">Channel 0..7, or -1 when nothing is known (no frames, no earlier word to inherit from).</param>
/// <param name="Probability">Mean probability of <paramref name="Speaker"/> over the word's window (0 when inherited blindly).</param>
/// <param name="OtherSpeaker">A second channel that is also above the threshold in the window (overlapped speech), or -1.</param>
/// <param name="OtherProbability">Its mean probability.</param>
/// <param name="IsRevisable">The window reaches past the diarization frontier: re-run the assignment once more audio was diarized.</param>
/// <param name="IsInherited">No channel reached the threshold; the label was taken from the previous word.</param>
public readonly record struct WordSpeaker(int Speaker, float Probability, int OtherSpeaker, float OtherProbability,
    bool IsRevisable, bool IsInherited)
{
    /// <summary>No information.</summary>
    public static WordSpeaker Unknown { get; } = new(-1, 0f, -1, 0f, true, false);

    /// <summary>True when a second speaker was active in the window.</summary>
    public bool HasOverlap => OtherSpeaker >= 0;

    /// <summary>True when <see cref="Speaker"/> is a real channel.</summary>
    public bool IsKnown => Speaker >= 0;
}

/// <summary>A segment as a range of words in the word list.</summary>
public readonly record struct SegmentWords(int FirstWord, int WordCount);

/// <summary>Speaker summary of a segment.</summary>
/// <param name="Speaker">Majority speaker by word duration (by summed activity for time-only segments); -1 if unknown.</param>
/// <param name="SecondsBySpeaker">Seconds attributed to each of the 8 channels.</param>
/// <param name="HasSpeakerChange">More than one speaker has a meaningful share (at least 15 % of the attributed time and 0.3 s).</param>
/// <param name="OverlapSeconds">Seconds of words that carry an overlapping second speaker.</param>
/// <param name="IsRevisable">At least one word is provisional.</param>
public sealed record SegmentSpeaker(int Speaker, IReadOnlyList<double> SecondsBySpeaker, bool HasSpeakerChange,
    double OverlapSeconds, bool IsRevisable)
{
    /// <summary>True when overlapped speech covers at least a fifth of the segment's attributed time.</summary>
    public bool HasOverlap => OverlapSeconds > 0 && OverlapSeconds >= 0.2 * SecondsBySpeaker.Sum();
}

/// <summary>Tuning of <see cref="SpeakerAssigner"/>.</summary>
public sealed record SpeakerAssignmentOptions
{
    /// <summary>A channel counts when its windowed mean is at least this.</summary>
    public float Threshold { get; init; } = 0.5f;

    /// <summary>
    /// Window length from the word's start: <c>[start, min(end, start + Anchor))</c>. The onset anchor keeps a word whose
    /// timing runs on (a trailing punctuation emission stretches the end into the next speaker's turn) from picking up
    /// the next speaker. Not tuned on real transcripts; 0.5 s is the starting value.
    /// </summary>
    public double AnchorSeconds { get; set; } = 0.5;

    /// <summary>Windows shorter than this are widened symmetrically-forward to it (zero-length word timings).</summary>
    public double MinWindowSeconds { get; set; } = 0.03;

    /// <summary>For words newer than the frontier: how far back to look for the last known speaker.</summary>
    public double ProvisionalLookbackSeconds { get; set; } = 0.3;

    /// <summary>Minimum share of the attributed time for a speaker change inside a segment.</summary>
    public double SpeakerChangeShare { get; set; } = 0.15;

    /// <summary>Minimum seconds of a second speaker for a speaker change inside a segment.</summary>
    public double SpeakerChangeSeconds { get; set; } = 0.3;
}

/// <summary>
/// Attaches speaker channels to ASR words and segments from the 10 ms probabilities. Pure: no model, no I/O; assigning
/// again after more audio was diarized revises the provisional words only in the sense that their windows are now complete.
/// </summary>
public static class SpeakerAssigner
{
    private const int N = SpeakerActivityTimeline.Speakers;
    private const double Frame = SpeakerActivityTimeline.FrameSeconds;

    /// <summary>
    /// One label per word, in word order. Per word: mean of the probabilities over <c>[start, min(end, start + anchor))</c>,
    /// top channel wins if it reaches the threshold; otherwise the previous word's speaker is inherited. Words whose
    /// window reaches past the timeline's frontier are flagged <see cref="WordSpeaker.IsRevisable"/>; those with no frames
    /// at all get the speaker active at the frontier (or the previous word's).
    /// </summary>
    public static WordSpeaker[] AssignWords(SpeakerActivityTimeline timeline, IReadOnlyList<WordTiming> words,
        SpeakerAssignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(words);
        options ??= new SpeakerAssignmentOptions();
        var result = new WordSpeaker[words.Count];
        long frontier = timeline.FrameCount;
        long first = timeline.FirstFrame;
        Span<float> mean = stackalloc float[N];
        int previous = -1;

        for (int i = 0; i < words.Count; i++)
        {
            double t0 = words[i].StartSeconds;
            double t1 = Math.Max(words[i].EndSeconds, t0);
            double end = Math.Min(t1, t0 + options.AnchorSeconds);
            end = Math.Max(end, t0 + options.MinWindowSeconds);
            long f0 = (long)Math.Floor(t0 / Frame + 1e-9);
            long f1 = Math.Max(f0 + 1, (long)Math.Ceiling(end / Frame - 1e-9));
            bool revisable = f1 > frontier;

            WordSpeaker label;
            int n = timeline.Average(f0, f1, mean);
            if (n == 0)
            {
                label = f0 >= frontier
                    ? Provisional(timeline, options, previous)
                    : f0 < first ? new WordSpeaker(previous, 0f, -1, 0f, false, previous >= 0) : WordSpeaker.Unknown;
                if (f0 >= frontier) label = label with { IsRevisable = true };
            }
            else
            {
                label = FromMean(mean, options.Threshold, previous, revisable);
            }

            result[i] = label;
            if (label.Speaker >= 0) previous = label.Speaker;
        }
        return result;
    }

    private static WordSpeaker FromMean(ReadOnlySpan<float> mean, float threshold, int previous, bool revisable)
    {
        int top = 0;
        for (int k = 1; k < N; k++) if (mean[k] > mean[top]) top = k;
        if (mean[top] < threshold)
            return new WordSpeaker(previous, 0f, -1, 0f, revisable, IsInherited: previous >= 0);

        int other = -1;
        for (int k = 0; k < N; k++)
            if (k != top && mean[k] >= threshold && (other < 0 || mean[k] > mean[other])) other = k;
        return new WordSpeaker(top, mean[top], other, other >= 0 ? mean[other] : 0f, revisable, false);
    }

    /// <summary>Label for a word that starts after the frontier: the speaker over the last frames, else the previous word's.</summary>
    private static WordSpeaker Provisional(SpeakerActivityTimeline timeline, SpeakerAssignmentOptions options, int previous)
    {
        long end = timeline.FrameCount;
        long from = end - Math.Max(1, (long)Math.Round(options.ProvisionalLookbackSeconds / Frame));
        Span<float> mean = stackalloc float[N];
        if (timeline.Average(from, end, mean) > 0)
        {
            int top = 0;
            for (int k = 1; k < N; k++) if (mean[k] > mean[top]) top = k;
            if (mean[top] >= options.Threshold)
                return new WordSpeaker(top, mean[top], -1, 0f, true, false);
        }
        return new WordSpeaker(previous, 0f, -1, 0f, true, previous >= 0);
    }

    /// <summary>
    /// Segment summaries from word labels: majority speaker by word duration, speaker-change and overlap flags.
    /// A segment with no words gets <c>Speaker = -1</c>.
    /// </summary>
    public static SegmentSpeaker[] AssignSegments(IReadOnlyList<WordTiming> words, IReadOnlyList<WordSpeaker> labels,
        IReadOnlyList<SegmentWords> segments, SpeakerAssignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(segments);
        if (labels.Count != words.Count)
            throw new ArgumentException("labels must have one entry per word.", nameof(labels));
        options ??= new SpeakerAssignmentOptions();

        var result = new SegmentSpeaker[segments.Count];
        for (int s = 0; s < segments.Count; s++)
        {
            var seg = segments[s];
            if (seg.FirstWord < 0 || seg.WordCount < 0 || seg.FirstWord + seg.WordCount > words.Count)
                throw new ArgumentOutOfRangeException(nameof(segments), $"Segment {s} ({seg}) is outside the word list.");
            var by = new double[N];
            double overlap = 0;
            bool revisable = false;
            for (int w = seg.FirstWord; w < seg.FirstWord + seg.WordCount; w++)
            {
                var l = labels[w];
                revisable |= l.IsRevisable;
                if (!l.IsKnown) continue;
                double d = Math.Max(words[w].EndSeconds - words[w].StartSeconds, 0.01);
                by[l.Speaker] += d;
                if (l.HasOverlap) overlap += d;
            }
            result[s] = Summarize(by, overlap, revisable, options);
        }
        return result;
    }

    /// <summary>
    /// Segment summary straight from the timeline, for segments without word timings: each frame counts for every
    /// channel above the threshold (so overlapped frames add to both), over <c>[startSeconds, endSeconds)</c>.
    /// </summary>
    public static SegmentSpeaker AssignSegment(SpeakerActivityTimeline timeline, double startSeconds, double endSeconds,
        SpeakerAssignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        options ??= new SpeakerAssignmentOptions();
        long f0 = (long)Math.Floor(startSeconds / Frame + 1e-9);
        long f1 = Math.Max(f0 + 1, (long)Math.Ceiling(endSeconds / Frame - 1e-9));
        long from = Math.Max(f0, timeline.FirstFrame);
        long to = Math.Min(f1, timeline.FrameCount);
        var by = new double[N];
        double overlap = 0;
        if (to > from)
        {
            var rows = timeline.CopyRange(from, to);
            for (int i = 0; i < rows.Length; i += N)
            {
                int active = 0;
                for (int k = 0; k < N; k++) if (rows[i + k] >= options.Threshold) active++;
                for (int k = 0; k < N; k++) if (rows[i + k] >= options.Threshold) by[k] += Frame;
                if (active >= 2) overlap += Frame;
            }
        }
        return Summarize(by, overlap, f1 > timeline.FrameCount, options);
    }

    private static SegmentSpeaker Summarize(double[] by, double overlap, bool revisable, SpeakerAssignmentOptions o)
    {
        double total = 0;
        int top = -1;
        for (int k = 0; k < N; k++)
        {
            total += by[k];
            if (by[k] > 0 && (top < 0 || by[k] > by[top])) top = k;
        }
        int significant = 0;
        for (int k = 0; k < N; k++)
            if (by[k] >= o.SpeakerChangeSeconds && by[k] >= o.SpeakerChangeShare * total) significant++;
        return new SegmentSpeaker(top, by, significant > 1, overlap, revisable);
    }
}
