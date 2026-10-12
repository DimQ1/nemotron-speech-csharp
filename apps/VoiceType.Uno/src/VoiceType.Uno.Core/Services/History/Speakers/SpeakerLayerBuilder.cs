using SpeechLib.Diarization;
using SpeechLib.Models;

namespace VoiceType.Uno.History;

/// <summary>Packs the model's 10 ms probabilities into the run file (8 bytes per 80 ms frame) and back.</summary>
public static class RunProbabilityCodec
{
    private const int N = SpeakerActivityTimeline.Speakers;

    /// <summary>10 ms frames per packed frame (the encoder's 80 ms).</summary>
    public const int Step = 8;

    /// <summary>Mean probability per channel over each 80 ms, scaled to a byte; 360 KB per hour.</summary>
    public static RunProbabilities Pack(SpeakerActivityTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        long frames = timeline.FrameCount;
        long first = timeline.FirstFrame;
        var packedCount = (frames - first + Step - 1) / Step;
        var bytes = new byte[packedCount * N];
        Span<float> mean = stackalloc float[N];
        for (long p = 0; p < packedCount; p++)
        {
            var from = first + p * Step;
            timeline.Average(from, Math.Min(from + Step, frames), mean);
            for (var k = 0; k < N; k++)
                bytes[p * N + k] = (byte)Math.Clamp((int)Math.Round(mean[k] * 255f), 0, 255);
        }
        return new RunProbabilities
        {
            FrameSec = Step * SpeakerActivityTimeline.FrameSeconds,
            Speakers = N,
            Encoding = RunProbabilities.U8Base64,
            Data = Convert.ToBase64String(bytes),
        };
    }

    /// <summary>Back to a 10 ms timeline (each packed frame repeated).</summary>
    public static SpeakerActivityTimeline ToTimeline(RunProbabilities probs)
    {
        ArgumentNullException.ThrowIfNull(probs);
        if (probs.Encoding != RunProbabilities.U8Base64 || probs.Speakers != N)
            throw new NotSupportedException($"Unsupported probability encoding '{probs.Encoding}' / {probs.Speakers} speakers.");
        var bytes = Convert.FromBase64String(probs.Data);
        var repeat = Math.Max(1, (int)Math.Round(probs.FrameSec / SpeakerActivityTimeline.FrameSeconds));
        var timeline = new SpeakerActivityTimeline();
        var chunk = new float[N * repeat * 256];
        var packedCount = bytes.Length / N;
        var filled = 0;
        for (var p = 0; p < packedCount; p++)
        {
            for (var r = 0; r < repeat; r++)
            {
                for (var k = 0; k < N; k++)
                    chunk[filled++] = bytes[p * N + k] / 255f;
            }
            if (filled == chunk.Length)
            {
                timeline.Append(chunk);
                filled = 0;
            }
        }
        if (filled > 0)
            timeline.Append(chunk.AsSpan(0, filled));
        return timeline;
    }

    /// <summary>A timeline from turns alone (runs without probabilities): 1 inside a turn, 0 outside.</summary>
    public static SpeakerActivityTimeline FromTurns(IReadOnlyList<RunTurn> turns, double durationSec)
    {
        var frames = (long)Math.Ceiling(durationSec / SpeakerActivityTimeline.FrameSeconds);
        foreach (var t in turns)
            frames = Math.Max(frames, (long)Math.Ceiling(t.End / SpeakerActivityTimeline.FrameSeconds));
        var matrix = new float[frames * N];
        foreach (var t in turns)
        {
            if ((uint)t.Speaker >= N)
                continue;
            var from = Math.Max(0, (long)Math.Floor(t.Start / SpeakerActivityTimeline.FrameSeconds));
            var to = Math.Min(frames, (long)Math.Ceiling(t.End / SpeakerActivityTimeline.FrameSeconds));
            for (var f = from; f < to; f++)
                matrix[f * N + t.Speaker] = 1f;
        }
        return SpeakerActivityTimeline.FromMatrix(matrix);
    }

    public static SpeakerActivityTimeline Timeline(DiarizationRunDocument run) =>
        run.Probs is { Data.Length: > 0 } probs ? ToTimeline(probs) : FromTurns(run.Turns, run.DurationSec);
}

/// <summary>A built layer and how its word times were obtained.</summary>
public sealed record SpeakerLayerResult(SpeakerLayerDocument Document, string WordTiming, int Words, int KnownWords);

/// <summary>
/// Labels the words of a transcript version with the channels of a diarization run (design §10.5). Pure and fast
/// (milliseconds), no model. The word times are the decoder's exact times when the transcript has them
/// (<c>wordTiming: "decoder"</c>), otherwise the estimated ones spread over the segment.
/// </summary>
public static class SpeakerLayerBuilder
{
    /// <summary>Version of this algorithm (<see cref="SpeakerLayerInfo.Assigner"/>).</summary>
    public const int AssignerVersion = 1;

    /// <summary>Share of words with decoder times from which the layer counts as exact.</summary>
    private const double DecoderShare = 0.9;

    public static SpeakerLayerResult Build(
        DiarizationRunDocument run, TranscriptDocument transcript, string versionId, SpeakerAssignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(transcript);
        return Build(RunProbabilityCodec.Timeline(run), run.Id, transcript, versionId, options);
    }

    public static SpeakerLayerResult Build(
        SpeakerActivityTimeline timeline, string runId, TranscriptDocument transcript, string versionId, SpeakerAssignmentOptions? options = null)
    {
        var words = new List<WordTiming>();
        var ranges = new List<SegmentWords>();
        var decoderWords = 0;
        foreach (var segment in transcript.Segments)
        {
            var textWords = TextTokens.SplitWords(segment.Text);
            var timed = segment.Words.Count == textWords.Count && textWords.Count > 0
                ? segment.Words
                : TranscriptTimings.Distribute(textWords, segment.Start, Math.Max(segment.End, segment.Start));
            if (ReferenceEquals(timed, segment.Words) && segment.WordTiming == WordTimingKinds.Decoder)
                decoderWords += textWords.Count;

            ranges.Add(new SegmentWords(words.Count, textWords.Count));
            for (var i = 0; i < textWords.Count; i++)
                words.Add(new WordTiming { Word = textWords[i], StartSeconds = timed[i].Start, EndSeconds = timed[i].End });
        }

        var labels = SpeakerAssigner.AssignWords(timeline, words, options);
        var layer = new SpeakerLayerDocument { Version = versionId, Run = runId, Assigner = AssignerVersion };
        var known = 0;
        for (var s = 0; s < transcript.Segments.Count; s++)
        {
            var segment = transcript.Segments[s];
            var range = ranges[s];
            var item = new SpeakerLayerSegment { Index = segment.Index, Start = segment.Start, End = segment.End };
            var seconds = new double[SpeakerActivityTimeline.Speakers];
            for (var w = 0; w < range.WordCount; w++)
            {
                var label = labels[range.FirstWord + w];
                item.Words.Add(label.Speaker);
                if (!label.IsKnown)
                    continue;
                known++;
                seconds[label.Speaker] += Math.Max(words[range.FirstWord + w].EndSeconds - words[range.FirstWord + w].StartSeconds, 0.01);
                if (label.HasOverlap)
                    (item.Overlap ??= []).Add([w, label.OtherSpeaker]);
            }
            var summary = SpeakerAssigner.AssignSegments(words, labels, [range], options)[0];
            item.Speaker = summary.Speaker;
            if (summary.HasSpeakerChange)
                item.Change = true;
            layer.Segments.Add(item);
        }

        var kind = words.Count > 0 && decoderWords >= DecoderShare * words.Count ? WordTimingKinds.Decoder : WordTimingKinds.Estimated;
        layer.WordTiming = kind;
        return new SpeakerLayerResult(layer, kind, words.Count, known);
    }
}
