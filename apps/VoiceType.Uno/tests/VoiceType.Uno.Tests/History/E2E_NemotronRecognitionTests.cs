using System.Text;
using System.Text.RegularExpressions;
using SpeechLib;
using SpeechLib.PostProcessing;
using SpeechLib.Providers;
using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using Xunit;
using Xunit.Abstractions;

namespace VoiceType.Uno.Tests.History;

/// <summary>
/// The Nemotron recognizer of the apps (what <see cref="RecognizerFactory"/> builds) through the History code with the real model:
/// <see cref="OfflineTranscriber"/> (re-recognition, resume from every checkpoint) and the live path
/// (<see cref="UtterancePipeline"/> + <see cref="LiveSegmentTimer"/>, fed in capture-sized batches). Every segment must carry the
/// decoder's word times, the live and the offline path must agree, and the text must be the plain recognizer's. Every run uses a
/// recognizer of its own: the gain control keeps what it learned about the microphone, so a second run on the same recognizer would
/// start with another gain. Skipped without the Nemotron INT4 model or the Common Voice test clips.
/// </summary>
public sealed class E2E_NemotronRecognitionTests(ITestOutputHelper output)
{
    private static readonly Regex Tag = new(@"<\w{2,3}(-\w{2,4})?>", RegexOptions.Compiled);
    private const int Rate = 16000;

    private static readonly PostProcessingChain FinalPostProcessing =
        new PostProcessingChain().Add(new LanguageTagStripper()).Add(new WhitespaceNormalizer());

    private static IStreamingSpeechRecognizer NewRecognizer() => RecognizerFactory.Create(new RecognizerFactoryOptions
    {
        ModelPath = NemotronAssets.ModelFolder ?? "",
        ExecutionProvider = "cpu",
        Language = "en",
    });

    /// <summary>
    /// The recognizer without the gain control, for the tests of the resume points: a recognizer that starts afresh also starts with a
    /// fresh gain, whose first loud frame sets the level (a quiet click in front of a sentence boosts the sentence 20 times and the model
    /// loses it, which has nothing to do with where the run resumes); and the transcript of a resumed run is then exactly the continuous one.
    /// </summary>
    private static IStreamingSpeechRecognizer NewRecognizerWithoutGain() => new SpeechLib.Nemotron.NemotronUtteranceRecognizer(
        NemotronAssets.ModelFolder ?? "", "cpu", "en");

    private static string Normalize(string text) => Regex.Replace(Tag.Replace(text, " "), @"\s+", " ").Trim();

    /// <summary>Four sentences with pauses of 3, 1.5 and 2.5 s (room tone) in between: ends of utterances by silence and by language tag.</summary>
    private static float[] Conversation() => NemotronAssets.Concat(
        NemotronAssets.LoadClip("cv17/en/0001.wav"), NemotronAssets.RoomTone(3, 1),
        NemotronAssets.LoadClip("cv17/en/0002.wav"), NemotronAssets.RoomTone(1.5, 2),
        NemotronAssets.LoadClip("cv17/en/0006.wav"), NemotronAssets.RoomTone(2.5, 3),
        NemotronAssets.LoadClip("cv17/en/0010.wav"));

    /// <summary>The text of the plain recognizer interface over the same audio, on a recognizer of its own.</summary>
    private static string PlainText(float[] audio)
    {
        using var recognizer = NewRecognizer();
        var text = new StringBuilder();
        for (var i = 0; i < audio.Length; i += recognizer.ChunkSamples)
            text.Append(recognizer.ProcessAudio(audio[i..Math.Min(audio.Length, i + recognizer.ChunkSamples)]));
        text.Append(recognizer.Flush());
        return Normalize(text.ToString());
    }

    private static void AssertDecoderTimed(IReadOnlyList<TranscriptSegment> segments, double audioSeconds)
    {
        Assert.NotEmpty(segments);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            Assert.Equal(WordTimingKinds.Decoder, segment.WordTiming);
            Assert.Equal(TextTokens.SplitWords(segment.Text).Count, segment.Words.Count);
            Assert.True(segment.Start >= 0 && segment.End >= segment.Start && segment.End <= audioSeconds + 0.01,
                $"segment {i} {segment.Start:F3}-{segment.End:F3} lies outside 0-{audioSeconds:F2} s");
            if (i > 0)
                Assert.True(segment.Start >= segments[i - 1].End - 1e-6, $"segment {i} starts before segment {i - 1} ends");
            for (var w = 1; w < segment.Words.Count; w++)
                Assert.True(segment.Words[w].Start >= segment.Words[w - 1].Start, $"words of segment {i} are not in order");
        }
    }

    private static string[] Words(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Word-level edit distance.</summary>
    private static int WordErrors(string[] reference, string[] hypothesis)
    {
        var d = new int[reference.Length + 1, hypothesis.Length + 1];
        for (var i = 0; i <= reference.Length; i++)
            d[i, 0] = i;
        for (var j = 0; j <= hypothesis.Length; j++)
            d[0, j] = j;
        for (var i = 1; i <= reference.Length; i++)
        {
            for (var j = 1; j <= hypothesis.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1));
        }
        return d[reference.Length, hypothesis.Length];
    }

    [NemotronE2EFact]
    public void OfflineTranscriber_ShouldGiveDecoderTimedSegmentsWithThePlainText()
    {
        var audio = Conversation();
        using var recognizer = NewRecognizer();

        var result = OfflineTranscriber.Run(recognizer, audio);

        var segments = result.Document.Segments;
        output.WriteLine(string.Join("\n", segments.Select(s => $"[{s.Start:F2}-{s.End:F2}] {s.Text}")));
        AssertDecoderTimed(segments, audio.Length / (double)Rate);
        Assert.True(segments.Count >= 3, "four sentences with pauses should give at least three segments");
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal(PlainText(audio), Normalize(result.Document.Text));
        Assert.DoesNotContain(segments, s => s.Text.StartsWith('.') || s.Text.StartsWith('?'));      // a closing mark never leads a segment
    }

    [NemotronE2EFact]
    public void OfflineTranscriber_ShouldResumeFromEveryCheckpointWithoutLosingAnything() => AssertResumesFromEveryCheckpoint(Conversation(), "football", "so rude");

    /// <summary>Sentences half a second apart: the model ends them with a language tag when the next one begins, so the Final is reported with the next speech.</summary>
    [NemotronE2EFact]
    public void OfflineTranscriber_ShouldResumeFromEveryCheckpointOfSentencesThatFollowEachOtherClosely() =>
        AssertResumesFromEveryCheckpoint(NemotronAssets.Concat(
            NemotronAssets.LoadClip("cv17/en/0001.wav"), NemotronAssets.RoomTone(0.5, 4),
            NemotronAssets.LoadClip("cv17/en/0002.wav"), NemotronAssets.RoomTone(0.5, 5),
            NemotronAssets.LoadClip("cv17/en/0006.wav"), NemotronAssets.RoomTone(0.5, 6),
            NemotronAssets.LoadClip("cv17/en/0010.wav")), "football", "so rude");

    /// <summary>
    /// A dense conversation: the clips are cut to their speech (the recordings carry a few tenths of a second of their own silence) and
    /// follow each other after 0.1 to 0.25 s of room tone, less than the margin a checkpoint behind the last word would need.
    /// </summary>
    [NemotronE2EFact]
    public void OfflineTranscriber_ShouldResumeFromEveryCheckpointOfASentencesThatFollowEachOtherAlmostWithoutAPause() =>
        AssertResumesFromEveryCheckpoint(NemotronAssets.Concat(
            Trimmed("cv17/en/0001.wav"), NemotronAssets.RoomTone(0.15, 7),
            Trimmed("cv17/en/0002.wav"), NemotronAssets.RoomTone(0.25, 8),
            Trimmed("cv17/en/0006.wav"), NemotronAssets.RoomTone(0.1, 9),
            Trimmed("cv17/en/0010.wav")), "football", "so ru", minCheckpoints: 1);        // cut right behind its last frame, "rude" may come out as "ru"

    /// <summary>A clip cut to its speech: the 10 ms frames below -46 dBFS at both ends are dropped (40 ms are kept).</summary>
    private static float[] Trimmed(string clip)
    {
        var samples = NemotronAssets.LoadClip(clip);
        const int frame = 160;
        bool Loud(int f) => Math.Sqrt(samples.Skip(f * frame).Take(frame).Sum(x => (double)x * x) / frame) > 0.005;
        int first = 0, last = samples.Length / frame - 1;
        while (first < last && !Loud(first))
            first++;
        while (last > first && !Loud(last))
            last--;
        var from = Math.Max(0, (first - 4) * frame);
        var to = Math.Min(samples.Length, (last + 5) * frame);
        return samples[from..to];
    }

    private static double RmsAround(float[] audio, long sample, double seconds)
    {
        var from = (int)Math.Max(0, sample - seconds * Rate);
        var to = (int)Math.Min(audio.Length, sample + seconds * Rate);
        return Math.Sqrt(audio.Skip(from).Take(to - from).Sum(x => (double)x * x) / Math.Max(1, to - from));
    }

    private void AssertResumesFromEveryCheckpoint(float[] audio, string wordOfASentenceInTheMiddle, string textEnd, int minCheckpoints = 2)
    {
        var checkpoints = new List<OfflineCheckpoint>();
        using var first = NewRecognizerWithoutGain();
        var continuous = OfflineTranscriber.Run(first, audio, new OfflineTranscriptionOptions { Checkpoint = checkpoints.Add });
        output.WriteLine("continuous: " + string.Join(" | ", continuous.Document.Segments.Select(s => $"[{s.Start:F2}-{s.End:F2}] {s.Text}")));
        output.WriteLine("checkpoints: " + string.Join(", ", checkpoints.Select(c => $"{c.Sample / (double)Rate:F2} s ({c.Segments.Count} segments)")));

        Assert.True(checkpoints.Count >= minCheckpoints, $"every segment committed by a Final but the first should offer a checkpoint in front of it, got {checkpoints.Count}");
        foreach (var checkpoint in checkpoints)
        {
            // the resume point is in silence: nothing of a sentence is cut in two (the room tone is about -69 dBFS, speech far above -50)
            Assert.True(RmsAround(audio, checkpoint.Sample, 0.04) < 0.003, $"the checkpoint at {checkpoint.Sample / (double)Rate:F2} s is not in silence");

            // a recognizer that starts afresh at the checkpoint, the committed segments of the checkpoint: what the job runner does
            using var recognizer = NewRecognizerWithoutGain();
            var resumed = OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions
            {
                StartSample = checkpoint.Sample,
                CommittedSegments = checkpoint.Segments,
            });

            output.WriteLine($"resume at {checkpoint.Sample / (double)Rate:F2} s: \"{Normalize(resumed.Document.Text)}\"");
            AssertDecoderTimed(resumed.Document.Segments, audio.Length / (double)Rate);
            Assert.Equal(checkpoint.Segments.Select(s => s.Text), resumed.Document.Segments.Take(checkpoint.Segments.Count).Select(s => s.Text));
            // what the checkpoint holds is the continuous run's, punctuation included: the closing marks that the model wrote late
            // (with the next speech) are in it, a run that resumes behind them cannot produce them
            Assert.Equal(continuous.Document.Segments.Take(checkpoint.Segments.Count).Select(s => s.Text), checkpoint.Segments.Select(s => s.Text));

            // no sentence is lost, repeated or split: the same number of segments, each close to the continuous one. A fresh stream decodes
            // what it hears a little differently ("should be all right" / "Should be alright", "they're" / "we are"): that is the model,
            // not the checkpoint, so a segment may differ in up to half of its words, but a missing sentence cannot hide in it
            Assert.Equal(continuous.Document.Segments.Count, resumed.Document.Segments.Count);
            for (var i = checkpoint.Segments.Count; i < resumed.Document.Segments.Count; i++)
            {
                var reference = Words(continuous.Document.Segments[i].Text);
                var errors = WordErrors(reference, Words(resumed.Document.Segments[i].Text));
                Assert.True(errors * 2 <= reference.Length, $"segment {i} after resuming at {checkpoint.Sample / (double)Rate:F2} s: \"{resumed.Document.Segments[i].Text}\" instead of \"{continuous.Document.Segments[i].Text}\"");
            }
            Assert.Contains(wordOfASentenceInTheMiddle, Words(resumed.Document.Text));
            Assert.Contains(textEnd, Normalize(resumed.Document.Text), StringComparison.OrdinalIgnoreCase);
        }
    }

    [NemotronE2EFact]
    public void LivePath_ShouldComposeTheTextAndTheSegmentsLikeTheOfflinePath()
    {
        var audio = Conversation();
        using var offlineRecognizer = NewRecognizer();
        var offline = OfflineTranscriber.Run(offlineRecognizer, audio).Document;
        var plain = PlainText(audio);

        // the live loop: capture-sized batches (50 ms) through the pipeline, the timer fed as LiveRecordingSession feeds it
        using var recognizer = NewRecognizer();
        recognizer.ResetStreamingState();
        var pipeline = RecognitionPipelineFactory.Create(recognizer);
        Assert.IsType<UtterancePipeline>(pipeline);
        var timer = new LiveSegmentTimer(Rate, utteranceCommits: true, ((IUtteranceStreamingRecognizer)recognizer).StopHistoryEouSeconds, onsetLeadSeconds: 0.5);
        var displays = new List<string>();
        const int batchSamples = 800;
        for (var i = 0; i < audio.Length; i += batchSamples)
        {
            var batch = audio[i..Math.Min(audio.Length, i + batchSamples)];
            var step = pipeline.Process(batch);
            timer.OnBatch(batch.Length, step.Tail, step.Committed, LiveSegmentTimer.LevelDb(batch), step.CommittedWords, step.CommittedContinuesPrevious);
            if (!string.IsNullOrEmpty(step.Display))
                displays.Add(Normalize(step.Display));
        }
        var tail = pipeline.Flush();
        timer.OnFlush(tail.Committed, tail.CommittedWords, tail.CommittedContinuesPrevious);
        var final = FinalPostProcessing.Execute(pipeline.CommittedText);

        output.WriteLine("live: " + final);
        Assert.Equal(plain, Normalize(final));
        Assert.Equal(offline.Segments.Count, timer.Segments.Count);
        for (var i = 0; i < offline.Segments.Count; i++)
        {
            Assert.Equal(offline.Segments[i].Text, timer.Segments[i].Text);
            Assert.Equal((offline.Segments[i].Start, offline.Segments[i].End), (timer.Segments[i].Start, timer.Segments[i].End));
            Assert.Equal(offline.Segments[i].Words.Select(w => (w.Text, w.Start, w.End)), timer.Segments[i].Words.Select(w => (w.Text, w.Start, w.End)));
        }
        AssertDecoderTimed(timer.Segments, audio.Length / (double)Rate);

        // what the user watches only ever grows: every token is final, nothing is shown twice or taken back
        for (var i = 1; i < displays.Count; i++)
            Assert.True(displays[i].StartsWith(displays[i - 1], StringComparison.Ordinal), $"display {i - 1} \"{displays[i - 1]}\" is not a prefix of \"{displays[i]}\"");
        Assert.Equal(plain, displays[^1]);
    }
}
