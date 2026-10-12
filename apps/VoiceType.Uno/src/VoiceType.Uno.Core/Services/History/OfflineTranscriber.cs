using System.Text;
using System.Text.RegularExpressions;
using SpeechLib;
using SpeechLib.Models;
using SpeechLib.PostProcessing;

namespace VoiceType.Uno.History;

/// <summary>Options of one <see cref="OfflineTranscriber.Run"/> call.</summary>
public sealed class OfflineTranscriptionOptions
{
    /// <summary>Samples per recognizer call; 0 = <see cref="IStreamingSpeechRecognizer.ChunkSamples"/>.</summary>
    public int BatchSamples { get; init; }

    /// <summary>Resume point: first sample to feed (a checkpoint of an earlier run).</summary>
    public long StartSample { get; init; }

    /// <summary>Segments committed before <see cref="StartSample"/> (from the checkpoint's part file).</summary>
    public IReadOnlyList<TranscriptSegment>? CommittedSegments { get; init; }

    /// <summary>Plain (non-utterance) recognizers: audio without new text that closes a segment.</summary>
    public double PauseSeconds { get; init; } = 1.0;

    /// <summary>Plain recognizers: a segment is closed at the next text after this length.</summary>
    public double MaxSegmentSeconds { get; init; } = 30.0;

    /// <summary>
    /// Snap segment bounds to the speech energy around the recognizer's estimate
    /// (<see cref="SpeechBounds"/>): streaming commits see an utterance's start late and its end
    /// only after the end-of-utterance pause.
    /// </summary>
    public bool RefineBounds { get; init; } = true;

    /// <summary>
    /// Use the exact word times of the recognizer's decoder when it provides them
    /// (<see cref="ITimedUtteranceRecognizer"/>): segment bounds come from the first and last word and
    /// the words are marked <c>decoder</c>. False = always estimate (comparison / fallback).
    /// </summary>
    public bool UseDecoderTimings { get; init; } = true;

    /// <summary>Called after every batch (on the calling thread).</summary>
    public Action<OfflineProgress>? Progress { get; init; }

    /// <summary>
    /// Called after a segment was committed at a point where a fresh recognizer can resume
    /// (an end of utterance / a pause): persist the segments and the sample.
    /// </summary>
    public Action<OfflineCheckpoint>? Checkpoint { get; init; }
}

public readonly record struct OfflineProgress(long ProcessedSamples, long TotalSamples, int SampleRate)
{
    public double Fraction => TotalSamples <= 0 ? 1 : Math.Clamp((double)ProcessedSamples / TotalSamples, 0, 1);
    public TimeSpan Processed => TimeSpan.FromSeconds((double)ProcessedSamples / SampleRate);
    public TimeSpan Total => TimeSpan.FromSeconds((double)TotalSamples / SampleRate);
}

/// <param name="Sample">First sample a resumed run feeds.</param>
/// <param name="Segments">Every segment committed before <paramref name="Sample"/>.</param>
public sealed record OfflineCheckpoint(long Sample, IReadOnlyList<TranscriptSegment> Segments);

/// <param name="DetectedLanguage">Most frequent language tag in the output (<c>&lt;ru-RU&gt;</c> → <c>ru</c>), or null.</param>
public sealed record OfflineTranscriptionResult(TranscriptDocument Document, string? DetectedLanguage);

/// <summary>
/// Re-recognition of a stored recording (design §9.1): feeds the whole audio to a streaming
/// recognizer as fast as it decodes — the same per-batch path as live recognition — and
/// builds transcript segments with timings from sample positions:
/// <list type="bullet">
/// <item>utterance recognizers (Parakeet TDT): one segment per committed utterance; start =
/// the first batch that produced partial text after the previous commit, end = the commit
/// batch minus <see cref="IUtteranceStreamingRecognizer.StopHistoryEouSeconds"/>;</item>
/// <item>plain recognizers (Nemotron): text deltas are grouped into segments closed by a pause
/// without new text, a sentence end after <see cref="OfflineTranscriptionOptions.MaxSegmentSeconds"/>
/// or the end of the audio.</item>
/// </list>
/// Word timings are estimated by spreading each segment's span over its words by length.
/// Cancellation is checked between batches. A run can resume from an
/// <see cref="OfflineCheckpoint"/> with a fresh recognizer: checkpoints are taken only at
/// an end of utterance or a pause, where the recognizer has emitted everything it heard.
/// </summary>
public static partial class OfflineTranscriber
{
    private static readonly PostProcessingChain Cleanup =
        new PostProcessingChain().Add(new LanguageTagStripper()).Add(new WhitespaceNormalizer());

    /// <summary>Shortest plausible time per word, for segments whose start was not observed.</summary>
    private const double WordSeconds = 0.25;

    [GeneratedRegex(@"<([A-Za-z]{2,3})(?:-\w{2,4})?>")]
    private static partial Regex LanguageTag();

    /// <summary>"alright ." → "alright." (a detached sentence mark).</summary>
    [GeneratedRegex(@"(?<=\w)\s+([.,!?;:…])(?=\s|$)")]
    private static partial Regex SpaceBeforePunctuation();

    public static OfflineTranscriptionResult Run(
        IStreamingSpeechRecognizer recognizer,
        float[] samples,
        OfflineTranscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(samples);
        options ??= new OfflineTranscriptionOptions();

        var state = new RunState(recognizer.SampleRate, samples.LongLength, options);
        recognizer.ResetStreamingState();

        if (recognizer is IUtteranceStreamingRecognizer utterance)
            RunUtterances(utterance, samples, state, cancellationToken);
        else
            RunPlain(recognizer, samples, state, cancellationToken);

        var segments = state.Segments;
        if (options.RefineBounds)
            SpeechBounds.Refine(samples, recognizer.SampleRate, segments,
                minSilenceSeconds: 0.85 * (recognizer is IUtteranceStreamingRecognizer u ? u.StopHistoryEouSeconds : options.PauseSeconds));
        for (var i = 0; i < segments.Count; i++)
            segments[i].Index = i;
        var document = new TranscriptDocument
        {
            Segments = segments,
            Text = TranscriptDocument.JoinSegments(segments),
        };
        return new OfflineTranscriptionResult(document, state.DetectedLanguage());
    }

    private static void RunUtterances(
        IUtteranceStreamingRecognizer recognizer, float[] samples, RunState state, CancellationToken ct)
    {
        long eouSamples = (long)Math.Round(recognizer.StopHistoryEouSeconds * state.Rate);
        long lastCommit = state.Start;
        long? utteranceStart = null;

        foreach (var (position, batch) in Batches(samples, state, recognizer.ChunkSamples))
        {
            ct.ThrowIfCancellationRequested();
            var result = recognizer.ProcessUtterance(batch);
            long end = position + batch.Length;

            if (!string.IsNullOrWhiteSpace(result.Final))
            {
                long speechEnd = end - eouSamples;
                // No partial was seen (committed in the step it appeared): estimate the span
                // back from the commit by the number of words.
                long minimum = (long)(TextTokens.SplitWords(result.Final).Count * WordSeconds * state.Rate);
                long start = utteranceStart ?? Math.Max(lastCommit, speechEnd - minimum);
                state.AddSegment(result.Final, start, Math.Max(speechEnd, Math.Min(end, start + minimum)), result.FinalWords);
                lastCommit = end;
                utteranceStart = null;
                state.RaiseCheckpoint(end);
            }

            // Partial text after this step's commit belongs to the next utterance.
            if (utteranceStart is null && !string.IsNullOrWhiteSpace(result.Partial))
                utteranceStart = position;

            state.RaiseProgress(end);
        }

        ct.ThrowIfCancellationRequested();
        var tail = recognizer.FlushUtterance();
        var tailText = string.IsNullOrWhiteSpace(tail.Final) ? tail.Partial : tail.Final;
        if (!string.IsNullOrWhiteSpace(tailText))
        {
            long start = utteranceStart ?? lastCommit;
            state.AddSegment(tailText, start, Math.Max(start, state.Total),
                string.IsNullOrWhiteSpace(tail.Final) ? tail.PartialWords : tail.FinalWords);
        }
        state.RaiseProgress(state.Total);
    }

    private static void RunPlain(
        IStreamingSpeechRecognizer recognizer, float[] samples, RunState state, CancellationToken ct)
    {
        long pauseSamples = (long)Math.Round(state.Options.PauseSeconds * state.Rate);
        long maxSamples = (long)Math.Round(state.Options.MaxSegmentSeconds * state.Rate);
        var current = new StringBuilder();
        long segmentStart = state.Start;
        long lastTextEnd = state.Start;

        void Close(long end, bool checkpointable, long resumeSample)
        {
            if (current.Length > 0)
                state.AddSegment(current.ToString(), segmentStart, Math.Max(segmentStart, end));
            current.Clear();
            if (checkpointable)
                state.RaiseCheckpoint(resumeSample);
        }

        foreach (var (position, batch) in Batches(samples, state, recognizer.ChunkSamples))
        {
            ct.ThrowIfCancellationRequested();
            var delta = recognizer.ProcessAudio(batch);
            long end = position + batch.Length;

            if (!string.IsNullOrEmpty(delta) && Cleanup.Execute(delta).Length > 0)
            {
                if (current.Length == 0)
                    segmentStart = position;
                current.Append(delta);
                lastTextEnd = end;
                // Long monologue: close at a sentence end once the segment is long enough.
                // Not a checkpoint: the recognizer may still hold audio it has not emitted.
                if (end - segmentStart >= maxSamples && EndsSentence(current))
                    Close(lastTextEnd, checkpointable: false, resumeSample: 0);
            }
            else if (current.Length > 0 && end - lastTextEnd >= pauseSamples)
            {
                // A pause with no new text: everything heard so far has been emitted.
                Close(lastTextEnd, checkpointable: true, resumeSample: end);
            }

            state.RaiseProgress(end);
        }

        ct.ThrowIfCancellationRequested();
        var tail = recognizer.Flush();
        if (!string.IsNullOrEmpty(tail) && Cleanup.Execute(tail).Length > 0)
        {
            if (current.Length == 0)
                segmentStart = lastTextEnd;
            current.Append(tail);
            lastTextEnd = state.Total;
        }
        Close(lastTextEnd, checkpointable: false, resumeSample: 0);
        state.RaiseProgress(state.Total);
    }

    private static IEnumerable<(long Position, float[] Batch)> Batches(float[] samples, RunState state, int chunkSamples)
    {
        int size = state.Options.BatchSamples > 0 ? state.Options.BatchSamples : Math.Max(1, chunkSamples);
        for (long position = state.Start; position < samples.LongLength; position += size)
        {
            int count = (int)Math.Min(size, samples.LongLength - position);
            var batch = new float[count];
            Array.Copy(samples, position, batch, 0, count);
            yield return (position, batch);
        }
    }

    private static bool EndsSentence(StringBuilder text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '"' or '\'' or ')' or '»' or '”')
                continue;
            return c is '.' or '!' or '?' or '…' or '。' or '？' or '！';
        }
        return false;
    }

    private sealed class RunState
    {
        private readonly Dictionary<string, int> _languages = new(StringComparer.OrdinalIgnoreCase);

        public RunState(int rate, long total, OfflineTranscriptionOptions options)
        {
            if (rate <= 0)
                throw new ArgumentOutOfRangeException(nameof(rate), rate, "Recognizer sample rate must be positive.");
            Rate = rate;
            Total = total;
            Options = options;
            Start = Math.Clamp(options.StartSample, 0, total);
            if (options.CommittedSegments is { } committed)
                Segments.AddRange(committed.Select(Clone));
        }

        public int Rate { get; }
        public long Total { get; }
        public long Start { get; }
        public OfflineTranscriptionOptions Options { get; }
        public List<TranscriptSegment> Segments { get; } = [];

        public void AddSegment(string rawText, long startSample, long endSample, IReadOnlyList<WordTiming>? decoderWords = null)
        {
            foreach (Match match in LanguageTag().Matches(rawText))
            {
                var code = match.Groups[1].Value.ToLowerInvariant();
                if (code is not "unk")
                    _languages[code] = _languages.GetValueOrDefault(code) + 1;
            }

            var text = SpaceBeforePunctuation().Replace(Cleanup.Execute(rawText), "$1").Trim();
            // The sentence end of the previous utterance often arrives with the next commit
            // (". She'll be alright"): it belongs to the previous segment.
            var lead = LeadingPunctuation(text);
            if (lead > 0)
            {
                if (Segments.Count > 0)
                    AppendToLast(text[..lead].Replace(" ", "", StringComparison.Ordinal));
                text = text[lead..].TrimStart();
            }
            if (text.Length == 0 || !text.Any(char.IsLetterOrDigit))
            {
                if (text.Length > 0 && Segments.Count > 0)
                    AppendToLast(text.Replace(" ", "", StringComparison.Ordinal));
                return;
            }

            double start = Seconds(startSample), end = Seconds(Math.Min(endSample, Total));
            if (end < start)
                end = start;
            var textWords = TextTokens.SplitWords(text);
            var exact = Options.UseDecoderTimings
                ? DecoderWordTimings.Map(decoderWords, textWords, (double)Start / Rate)
                : null;
            if (exact is not null)
            {
                // Decoder alignment: the segment spans its first to its last word.
                Segments.Add(new TranscriptSegment
                {
                    Index = Segments.Count,
                    Start = exact[0].Start,
                    End = Math.Max(exact[0].Start, exact[^1].End),
                    Text = text,
                    Words = exact,
                    WordTiming = WordTimingKinds.Decoder,
                });
                return;
            }

            Segments.Add(new TranscriptSegment
            {
                Index = Segments.Count,
                Start = start,
                End = end,
                Text = text,
                Words = TranscriptTimings.Distribute(textWords, start, end),
                WordTiming = WordTimingKinds.Estimated,
            });
        }

        /// <summary>Length of the punctuation-only prefix (". ", "?", ". ,") of <paramref name="text"/>.</summary>
        private static int LeadingPunctuation(string text)
        {
            var i = 0;
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] is '.' or ',' or '!' or '?' or ';' or ':' or '…' or '。' or '？' or '！' or '、'))
                i++;
            // Only a separate token: ".5 kg" or "...and" are left alone.
            return i > 0 && (i == text.Length || char.IsWhiteSpace(text[i - 1])) ? i : 0;
        }

        private void AppendToLast(string punctuation)
        {
            if (punctuation.Length == 0)
                return;
            var last = Segments[^1];
            last.Text += punctuation;
            if (last.Words.Count > 0)
                last.Words[^1].Text += punctuation;
        }

        public void RaiseProgress(long processed) =>
            Options.Progress?.Invoke(new OfflineProgress(processed, Total, Rate));

        public void RaiseCheckpoint(long sample) =>
            Options.Checkpoint?.Invoke(new OfflineCheckpoint(sample, Segments.Select(Clone).ToList()));

        public string? DetectedLanguage() =>
            _languages.Count == 0 ? null : _languages.MaxBy(pair => pair.Value).Key;

        private double Seconds(long sample) => Math.Round((double)sample / Rate, 3);

        private static TranscriptSegment Clone(TranscriptSegment segment) =>
            HistoryJson.Clone(segment, HistoryJsonContext.Default.TranscriptSegment);
    }
}

/// <summary>
/// Moves segment bounds onto the speech in the audio. A streaming recognizer shows an
/// utterance's first partial only after some right context (up to ~2 s for the first word
/// after a pause) and commits it after the end-of-utterance pause, so the sample-based
/// estimates are late. Segments are consecutive utterances separated by pauses of at least
/// the end-of-utterance silence, so:
/// <list type="bullet">
/// <item>a segment starts at the first voiced frame after the previous segment's end (looking
/// back at most <see cref="StartLookBackSeconds"/> from the estimate);</item>
/// <item>it ends where the first pause of at least <c>minSilenceSeconds</c> begins (a shorter
/// pause did not end the utterance), looking at most <see cref="EndLookAheadSeconds"/> past
/// the estimate.</item>
/// </list>
/// Voiced = 20 ms frames above the local noise floor (15th percentile of the frames around the
/// segment, so a quieter speaker or clip is judged on its own level) + 12 dB. Word timings are
/// spread again over the new span. Without a clear dynamic range (noise, music), a matching
/// pause, or when the refined span is implausibly short for its words, the estimate stays.
/// </summary>
internal static class SpeechBounds
{
    private const double FrameSeconds = 0.02;
    private const double StartLookBackSeconds = 3.0;
    private const double StartLookAheadSeconds = 0.6;
    private const double EndLookAheadSeconds = 0.6;
    private const double MarginDb = 12;
    private const double ContextBeforeSeconds = 6.0;
    private const double ContextAfterSeconds = 2.0;
    /// <summary>A refined span shorter than this per word is not trusted.</summary>
    private const double MinSecondsPerWord = 0.12;

    public static void Refine(float[] samples, int rate, List<TranscriptSegment> segments, double minSilenceSeconds)
    {
        if (segments.Count == 0 || samples.Length < rate / 10)
            return;

        int frame = Math.Max(1, (int)(rate * FrameSeconds));
        var levels = FrameLevels(samples, frame);
        int frames = levels.Length;
        if (frames == 0)
            return;
        int silenceRun = Math.Max(1, (int)Math.Round(minSilenceSeconds / FrameSeconds));
        int F(double seconds) => Math.Clamp((int)Math.Round(seconds / FrameSeconds), 0, frames);
        double S(int f) => Math.Round(f * FrameSeconds, 3);

        double prevEnd = 0;
        foreach (var segment in segments)
        {
            if (segment.WordTiming == WordTimingKinds.Decoder)
            {
                prevEnd = segment.End; // exact word times: nothing to refine
                continue;
            }
            var voiced = Voiced(levels, F(segment.Start - ContextBeforeSeconds), F(segment.End + ContextAfterSeconds));
            if (voiced is null)
            {
                prevEnd = segment.End;
                continue;
            }
            int lower = F(Math.Max(prevEnd, segment.Start - StartLookBackSeconds));
            int onset = FirstVoiced(voiced, lower, F(segment.Start + StartLookAheadSeconds));
            double start = onset >= 0 ? S(onset) : segment.Start;

            int pause = FirstPause(voiced, F(start), F(segment.End + EndLookAheadSeconds), silenceRun);
            double end = pause >= 0 ? S(pause) : segment.End;

            start = Math.Max(prevEnd, start);
            int words = Math.Max(1, TextTokens.SplitWords(segment.Text).Count);
            if (end - start >= Math.Max(0.1, words * MinSecondsPerWord)
                && (Math.Abs(start - segment.Start) > 1e-9 || Math.Abs(end - segment.End) > 1e-9))
            {
                segment.Start = start;
                segment.End = end;
                segment.Words = TranscriptTimings.Distribute(segment.Words.Count > 0
                    ? segment.Words.Select(w => w.Text).ToList()
                    : TextTokens.SplitWords(segment.Text), segment.Start, segment.End);
            }
            prevEnd = segment.End;
        }
    }

    /// <summary>Level of every frame in dBFS.</summary>
    internal static double[] FrameLevels(float[] samples, int frame)
    {
        int frames = samples.Length / frame;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            int offset = f * frame;
            for (int k = 0; k < frame; k++)
            {
                double s = samples[offset + k];
                sum += s * s;
            }
            db[f] = 10 * Math.Log10(sum / frame + 1e-12);
        }
        return db;
    }

    /// <summary>
    /// Voiced flags for every frame (only [<paramref name="from"/>, <paramref name="to"/>) is
    /// judged; the rest is unvoiced), or null when that stretch has no dynamic range.
    /// </summary>
    internal static bool[]? Voiced(double[] levels, int from, int to)
    {
        from = Math.Clamp(from, 0, levels.Length);
        to = Math.Clamp(to, from, levels.Length);
        if (to - from < 10)
            return null;
        var sorted = levels[from..to];
        Array.Sort(sorted);
        double noise = sorted[(int)(sorted.Length * 0.15)];
        double loud = sorted[(int)(sorted.Length * 0.95)];
        if (loud - noise < MarginDb)
            return null;
        double threshold = Math.Max(noise + MarginDb, -60);
        var voiced = new bool[levels.Length];
        for (int f = from; f < to; f++)
            voiced[f] = levels[f] > threshold;
        return voiced;
    }

    private static int FirstVoiced(bool[] voiced, int from, int to)
    {
        for (int f = Math.Max(0, from); f < Math.Min(to, voiced.Length); f++)
        {
            if (voiced[f])
                return f;
        }
        return -1;
    }

    /// <summary>
    /// First frame after <paramref name="from"/> where a pause of <paramref name="run"/> unvoiced
    /// frames begins right after speech, starting no later than <paramref name="latest"/>; -1 if none.
    /// </summary>
    private static int FirstPause(bool[] voiced, int from, int latest, int run)
    {
        int silent = 0;
        bool spoke = false;
        for (int f = Math.Max(0, from); f < voiced.Length; f++)
        {
            if (voiced[f])
            {
                spoke = true;
                silent = 0;
                if (f > latest)
                    return -1; // still speaking past the window: keep the estimate
            }
            else if (spoke && ++silent >= run)
            {
                int pauseStart = f - run + 1;
                return pauseStart <= latest ? pauseStart : -1;
            }
        }
        // The audio ends during speech or a short pause.
        int end = voiced.Length - silent;
        return spoke && end <= latest ? end : -1;
    }
}

/// <summary>
/// Partial result of an interrupted re-recognition: the segments committed before the
/// checkpoint sample and that sample, stored as one transcript document next to the queue
/// (<c>jobs/&lt;jobId&gt;.part.json</c>) and written atomically — so the sample and the
/// segments can never disagree, whatever the queue file says.
/// </summary>
public static class OfflinePartFile
{
    public const string Suffix = ".part.json";
    private const string SampleField = "checkpointSample";

    public static string PathFor(string jobsDir, string jobId) => Path.Combine(jobsDir, jobId + Suffix);

    public static void Save(string path, OfflineCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var document = new TranscriptDocument
        {
            Version = "part",
            Segments = checkpoint.Segments.ToList(),
            Text = TranscriptDocument.JoinSegments(checkpoint.Segments),
            ExtensionData = new() { [SampleField] = JsonValues.From(checkpoint.Sample) },
        };
        HistoryJson.WriteAtomic(path, document, HistoryJsonContext.Default.TranscriptDocument);
    }

    /// <summary>The stored checkpoint, or null when the file is missing or unreadable (start over).</summary>
    public static OfflineCheckpoint? TryLoad(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            var document = HistoryJson.Read(path, HistoryJsonContext.Default.TranscriptDocument);
            if (document.ExtensionData?.TryGetValue(SampleField, out var sample) != true
                || !sample.TryGetInt64(out var value) || value < 0)
                return null;
            return new OfflineCheckpoint(value, document.Segments);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void Delete(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
