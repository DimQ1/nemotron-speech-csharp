using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceType.Uno.History;

/// <summary>
/// Turns the per-batch output of a live recognizer into timed transcript segments
/// (design §5.3). Times are file positions: the sum of the batches the recognizer saw,
/// which are exactly the batches written to the audio file (muted batches reach
/// neither), so segment times and audio positions share one timeline.
/// <list type="bullet">
/// <item><b>Start</b> = start of the first batch that produced a non-empty partial (or
/// committed text) after the previous commit, moved back by a small onset lead and — when
/// batch levels are supplied — to the start of the voiced stretch that partial belongs
/// to (at most 4 s back, never before the previous segment's end).</item>
/// <item><b>End</b> (utterance recognizers) = commit batch end minus the recognizer's
/// end-of-utterance look-ahead, but never later than the end of the last batch that
/// changed the partial — when an utterance is closed by the onset of the next one,
/// "commit minus EOU" lies far inside the pause.</item>
/// <item>Plain recognizers (no utterance commits) commit deltas every step; a segment
/// closes at a sentence end, after a pause without new text, or at a length cap.</item>
/// </list>
/// Word timings inside a segment are estimated by character length, like
/// <c>Transcriber.AddWordTimings</c> in SpeechLib without token counts.
/// Not thread-safe: fed from the recognition loop only.
/// </summary>
public sealed partial class LiveSegmentTimer
{
    private const double MinSegmentSeconds = 0.3;
    private const double FallbackSecondsPerWord = 0.45;

    // Voice activity from batch levels: a batch is voiced when it is this far above the
    // quietest level seen (and above an absolute floor); runs split at pauses this long.
    private const double VoiceMarginDb = 15;
    private const double MinVoiceDb = -50;
    private const double VoiceRunGapSeconds = 0.6;
    private const double MaxVoiceLeadSeconds = 4.0;

    private readonly double _commitLagSeconds;
    private readonly bool _utteranceCommits;
    private readonly long _plainPauseSamples;
    private readonly long _plainMaxSamples;
    private readonly long _onsetLeadSamples;
    private readonly List<TranscriptSegment> _segments = [];
    private readonly StringBuilder _plainBuffer = new();

    private long? _openStart;
    private long? _lastActivityEnd;
    private string _lastTail = "";
    private long _lastClosedEnd;

    private double _noiseFloorDb = double.PositiveInfinity;
    private long? _runStart;
    private long _lastVoicedEnd = long.MinValue / 2;
    private long? _openVoiceStart;

    /// <param name="sampleRate">Recognizer (and audio file) sample rate.</param>
    /// <param name="utteranceCommits">True for utterance-segmented recognizers (Parakeet TDT).</param>
    /// <param name="commitLagSeconds">
    /// How long after the end of speech an utterance commit arrives (the end-of-utterance
    /// silence, <c>IUtteranceStreamingRecognizer.StopHistoryEouSeconds</c>); 0 for plain recognizers.
    /// </param>
    /// <param name="plainPauseSeconds">Plain recognizers: silence (no new text) that closes a segment.</param>
    /// <param name="plainMaxSeconds">Plain recognizers: longest segment before it is closed at the last commit.</param>
    /// <param name="onsetLeadSeconds">
    /// Moved back from the first-partial batch (never before the previous segment's end):
    /// a recognizer shows its first word a little after the word began.
    /// </param>
    public LiveSegmentTimer(
        int sampleRate,
        bool utteranceCommits,
        double commitLagSeconds = 0,
        double plainPauseSeconds = 1.0,
        double plainMaxSeconds = 30.0,
        double onsetLeadSeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        _utteranceCommits = utteranceCommits;
        _commitLagSeconds = Math.Max(0, commitLagSeconds);
        _plainPauseSamples = (long)(plainPauseSeconds * sampleRate);
        _plainMaxSamples = (long)(plainMaxSeconds * sampleRate);
        _onsetLeadSamples = (long)(Math.Max(0, onsetLeadSeconds) * sampleRate);
    }

    public int SampleRate { get; }

    /// <summary>Samples fed so far (= audio file position).</summary>
    public long Position { get; private set; }

    /// <summary>First language tag (<c>&lt;en&gt;</c>) seen in the raw recognizer text, if any.</summary>
    public string? DetectedLanguage { get; private set; }

    public IReadOnlyList<TranscriptSegment> Segments => _segments;

    /// <summary>
    /// One recognizer batch of <paramref name="samples"/> samples.
    /// </summary>
    /// <param name="tail">The revisable, uncommitted text after this batch (null/empty when none).</param>
    /// <param name="committed">Raw text this batch committed (null when none).</param>
    /// <param name="levelDb">
    /// RMS level of the batch in dBFS, when known. Recognizers show the first words of an
    /// utterance late (Parakeet holds them for its onset context after a pause, up to
    /// ~4 s), so the segment start is moved back to the start of the voiced stretch the
    /// first partial belongs to.
    /// </param>
    public void OnBatch(int samples, string? tail, string? committed, double? levelDb = null)
    {
        if (samples < 0)
            throw new ArgumentOutOfRangeException(nameof(samples));

        var batchStart = Position;
        Position += samples;
        var batchEnd = Position;
        DetectLanguage(committed);
        DetectLanguage(tail);
        TrackVoice(batchStart, batchEnd, levelDb);

        var hasTail = !string.IsNullOrWhiteSpace(tail);
        var hasCommit = !string.IsNullOrWhiteSpace(committed);

        if (_utteranceCommits)
        {
            // The commit closes the previous utterance; a tail in the same batch already
            // belongs to the next one, so the close uses the activity seen before it.
            if (hasCommit)
            {
                var lagged = batchEnd - ToSamples(_commitLagSeconds);
                var end = _lastActivityEnd is { } activity ? Math.Min(lagged, activity) : lagged;
                CloseSegment(committed!, _openStart, end, batchEnd);
            }
        }
        else if (hasCommit)
        {
            Open(batchStart);
            _plainBuffer.Append(committed);
            _lastActivityEnd = batchEnd;
        }

        if (hasTail)
        {
            Open(batchStart);
            if (!string.Equals(tail, _lastTail, StringComparison.Ordinal))
                _lastActivityEnd = batchEnd;
        }
        else if (!hasCommit && (_utteranceCommits || _plainBuffer.Length == 0))
        {
            // A preview that vanished without a commit was noise; the next speech
            // starts the segment afresh.
            ResetOpen();
        }
        _lastTail = tail ?? "";

        if (!_utteranceCommits)
            ClosePlainIfDue(batchEnd);
    }

    /// <summary>Text the recognizer produced when the stream was flushed (end of session).</summary>
    public void OnFlush(string? committed)
    {
        DetectLanguage(committed);
        if (_utteranceCommits)
        {
            if (!string.IsNullOrWhiteSpace(committed))
                CloseSegment(committed, _openStart, _lastActivityEnd ?? Position, Position);
        }
        else
        {
            if (!string.IsNullOrEmpty(committed))
            {
                Open(Position);
                _plainBuffer.Append(committed);
            }
            ClosePlain(_lastActivityEnd ?? Position, Position);
        }
    }

    /// <summary>The transcript so far (segments with estimated word timings).</summary>
    public TranscriptDocument BuildDocument()
    {
        var doc = new TranscriptDocument
        {
            Segments = _segments.Select(Copy).ToList(),
        };
        doc.Text = TranscriptDocument.JoinSegments(doc.Segments);
        return doc;
    }

    /// <summary>Strips language tags and collapses whitespace, as the app's final post-processing does.</summary>
    public static string CleanText(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";
        var stripped = LanguageTag().Replace(raw, " ");
        return Whitespace().Replace(stripped, " ").Trim();
    }

    // ── internals ───────────────────────────────────────────────────────────

    private void ClosePlainIfDue(long batchEnd)
    {
        if (_plainBuffer.Length == 0)
            return;

        var text = CleanText(_plainBuffer.ToString());
        var sentenceEnd = text.Length > 0 && text[^1] is '.' or '?' or '!' or '…' or '。' or '？' or '！';
        if (sentenceEnd && _lastActivityEnd == batchEnd)
            ClosePlain(batchEnd, batchEnd);
        else if (_lastActivityEnd is { } activity && batchEnd - activity >= _plainPauseSamples)
            ClosePlain(activity, batchEnd);
        else if (_openStart is { } start && _lastActivityEnd is { } last && last - start >= _plainMaxSamples)
            ClosePlain(last, batchEnd);
    }

    private void ClosePlain(long end, long limit)
    {
        if (_plainBuffer.Length == 0)
        {
            ResetOpen();
            return;
        }
        var text = _plainBuffer.ToString();
        _plainBuffer.Clear();
        CloseSegment(text, _openStart, end, limit);
    }

    private void CloseSegment(string rawText, long? openStart, long end, long limit)
    {
        var text = CleanText(rawText);
        if (text.Length == 0)
        {
            ResetOpen();
            return;
        }

        var wordCount = Math.Max(1, TextTokens.SplitWords(text).Count);
        end = Math.Clamp(end, _lastClosedEnd, limit);
        long start;
        if (openStart is { } open)
        {
            start = open - _onsetLeadSamples;
            if (_openVoiceStart is { } voice && voice < start)
                start = Math.Max(voice, open - ToSamples(MaxVoiceLeadSeconds));
        }
        else
        {
            start = Math.Max(_lastClosedEnd, end - ToSamples(wordCount * FallbackSecondsPerWord));
        }
        start = Math.Clamp(start, _lastClosedEnd, limit);
        if (end - start < ToSamples(MinSegmentSeconds))
            end = Math.Min(limit, start + ToSamples(MinSegmentSeconds));
        if (end < start)
            end = start;

        var startSec = Round(ToSeconds(start));
        var endSec = Round(ToSeconds(end));
        _segments.Add(new TranscriptSegment
        {
            Index = _segments.Count,
            Start = startSec,
            End = endSec,
            Text = text,
            Words = TranscriptTimings.Distribute(TextTokens.SplitWords(text), startSec, endSec),
            WordTiming = WordTimingKinds.Estimated,
        });
        _lastClosedEnd = end;
        ResetOpen();
    }

    private void ResetOpen()
    {
        _openStart = null;
        _openVoiceStart = null;
        _lastActivityEnd = null;
    }

    /// <summary>Opens a segment at <paramref name="at"/>, remembering the voiced stretch it belongs to.</summary>
    private void Open(long at)
    {
        if (_openStart is not null)
            return;
        _openStart = at;
        _openVoiceStart = _runStart is { } run && at - _lastVoicedEnd < ToSamples(VoiceRunGapSeconds) ? run : null;
    }

    private void TrackVoice(long batchStart, long batchEnd, double? levelDb)
    {
        if (levelDb is not { } level || double.IsNaN(level))
            return;
        level = Math.Max(level, -100);
        _noiseFloorDb = Math.Min(_noiseFloorDb, level);
        if (level < Math.Max(MinVoiceDb, _noiseFloorDb + VoiceMarginDb))
            return;
        if (_runStart is null || batchStart - _lastVoicedEnd >= ToSamples(VoiceRunGapSeconds))
            _runStart = batchStart;
        _lastVoicedEnd = batchEnd;
    }

    /// <summary>RMS level of <paramref name="samples"/> in dBFS (−100 for silence).</summary>
    public static double LevelDb(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return -100;
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        var rms = Math.Sqrt(sum / samples.Length);
        return rms <= 1e-5 ? -100 : 20 * Math.Log10(rms);
    }

    private void DetectLanguage(string? raw)
    {
        if (DetectedLanguage is not null || string.IsNullOrEmpty(raw))
            return;
        var match = LanguageTag().Match(raw);
        if (match.Success)
            DetectedLanguage = match.Groups[1].Value.ToLowerInvariant();
    }

    private long ToSamples(double seconds) => (long)Math.Round(seconds * SampleRate);

    private double ToSeconds(long samples) => (double)samples / SampleRate;

    private static double Round(double seconds) => Math.Round(seconds, 3, MidpointRounding.AwayFromZero);

    private static TranscriptSegment Copy(TranscriptSegment s) => new()
    {
        Index = s.Index,
        Start = s.Start,
        End = s.End,
        Text = s.Text,
        Words = s.Words.Select(w => new TranscriptWord(w.Text, w.Start, w.End)).ToList(),
        WordTiming = s.WordTiming,
    };

    [GeneratedRegex(@"<(\w{2,3}(?:-\w{2,4})?)>", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{_segments.Count} segments @ {ToSeconds(Position):F2}s");
}
