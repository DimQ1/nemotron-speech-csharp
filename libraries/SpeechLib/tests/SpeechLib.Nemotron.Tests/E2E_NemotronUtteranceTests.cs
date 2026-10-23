using System.Text.RegularExpressions;
using SpeechLib.Models;
using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The utterance and word-time layer on the real models: the text equals the plain recognizer's, every Final has one timed
/// word per word of its tag-free text, times are ordered and inside the stream, the result does not depend on how the audio
/// is batched, the offline words equal the streaming words, and the word times of a synthetic two-voice dialogue are
/// accurate enough for speaker assignment. Skipped without the models, the test audio or the committed truth sample.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronUtteranceTests(NemotronModelCache models, ITestOutputHelper output)
{
    private static readonly Regex Tag = new(@"<\w{2,3}(-\w{2,4})?>", RegexOptions.Compiled);

    private sealed class UtteranceRun
    {
        public List<StreamingResult> Results { get; } = new();
        public IEnumerable<string> Finals => Results.Where(r => r.Final is not null).Select(r => r.Final!);
        public List<WordTiming> Words => Results.SelectMany(r => r.FinalWords ?? []).ToList();
        public string Tail => Results[^1].Partial;
        // one word per whitespace word of the transcript: the mark or the late piece that leads a Final belongs to the word before it
        public List<WordTiming> TranscriptWords
        {
            get
            {
                var words = new List<WordTiming>();
                foreach (var result in Results.Where(r => r.FinalWords is { Count: > 0 }))
                {
                    var ready = result.FinalWords!;
                    if (result.FinalContinuesPrevious && words.Count > 0)
                    {
                        var last = words[^1];
                        words[^1] = last with
                        {
                            Word = last.Word + ready[0].Word,
                            EndSeconds = ready[0].Word.Any(char.IsLetterOrDigit) ? Math.Max(last.EndSeconds, ready[0].EndSeconds) : last.EndSeconds,
                        };
                        words.AddRange(ready.Skip(1));
                    }
                    else
                    {
                        words.AddRange(ready);
                    }
                }
                return words;
            }
        }
        // the transcript the way a consumer assembles it: Finals joined with the continuation flag, then the last partial
        public string Text
        {
            get
            {
                var text = "";
                foreach (var result in Results.Where(r => r.Final is not null))
                    text = UtteranceText.Join(text, result.Final, result.FinalContinuesPrevious);
                return Normalize(UtteranceText.Join(text, Tail));
            }
        }
    }

    private static string Normalize(string text) => Regex.Replace(Tag.Replace(text, " "), @"\s+", " ").Trim();

    private static int WhitespaceWords(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static UtteranceRun Run(NemotronUtteranceRecognizer recognizer, IEnumerable<float[]> pieces)
    {
        var run = new UtteranceRun();
        foreach (var piece in pieces)
            run.Results.Add(recognizer.ProcessUtterance(piece));
        run.Results.Add(recognizer.FlushUtterance());
        return run;
    }

    private static NemotronUtteranceRecognizer Wrap(NemotronRnntRecognizer engine, NemotronUtteranceOptions? options = null) =>
        new(engine, options ?? new NemotronUtteranceOptions(), createOfflineEngine: null, ownsEngine: false);

    private static void SetLanguage(NemotronRnntRecognizer engine, GoldenCase golden)
    {
        if (golden.LangId is { } id)
            Assert.True(engine.TrySetLanguage(id));
    }

    // Words are ordered and inside the stream; a word with letters has a duration and starts after the word before it (a mark that
    // leads an utterance sits zero-width on the start of its first word, and the end of the stream may cut the last word short).
    private static void AssertWordsAreSane(IReadOnlyList<WordTiming> words, double streamSeconds, string context)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            Assert.True(w.StartSeconds >= 0 && w.EndSeconds >= w.StartSeconds && w.EndSeconds <= streamSeconds + 1e-6, $"{context}: word {i} '{w.Word}' {w.StartSeconds:F3}-{w.EndSeconds:F3} outside 0-{streamSeconds:F3}");
            var spoken = w.Word.Any(char.IsLetterOrDigit);
            var cutByTheEnd = w.StartSeconds >= streamSeconds - 1e-6;
            if (spoken && !cutByTheEnd)
                Assert.True(w.EndSeconds > w.StartSeconds, $"{context}: word {i} '{w.Word}' has no duration ({w.StartSeconds:F3})");
            if (i > 0)
            {
                Assert.True(w.StartSeconds >= words[i - 1].StartSeconds - 1e-9, $"{context}: word {i} '{w.Word}' starts before the previous word");
                Assert.True(w.StartSeconds >= words[i - 1].EndSeconds - 1e-9, $"{context}: word {i} '{w.Word}' overlaps the previous word");
                if (spoken && words[i - 1].Word.Any(char.IsLetterOrDigit) && !cutByTheEnd)
                    Assert.True(w.StartSeconds > words[i - 1].StartSeconds, $"{context}: word {i} '{w.Word}' starts where the previous word starts ({w.StartSeconds:F3})");
            }
            Assert.NotNull(w.Tokens);
            Assert.DoesNotContain("<", w.Word);
        }
    }

    // ── text, counts, timeline ──────────────────────────────────────────

    [NemotronGoldenTheory]
    [MemberData(nameof(GoldenData.Parity), MemberType = typeof(GoldenData))]
    public void Utterances_ShouldCarryThePlainTextWithOneTimedWordPerWord(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var engine = models.Get(variant);
        SetLanguage(engine, golden);
        var audio = golden.LoadAudio();
        var n = engine.ChunkSamples;

        var plain = Replay.Run(engine, audio, n);                    // the plain recognizer's text
        engine.ResetStreamingState();
        using var recognizer = Wrap(engine);
        var run = Run(recognizer, TestAudio.Split(audio, n));

        Assert.Equal(Normalize(plain.Text.ToString()), run.Text);
        foreach (var result in run.Results.Where(r => r.Final is not null))
        {
            Assert.Equal(WhitespaceWords(result.Final!), result.FinalWords!.Count);
            Assert.Equal(Normalize(result.Final!), string.Join(' ', result.FinalWords.Select(w => w.Word)).Trim());
        }
        Assert.DoesNotContain(run.Results, r => r.Partial.Contains('<'));
        AssertWordsAreSane(run.Words, audio.Length / 16000.0, name);
    }

    [NemotronGoldenTheory]
    [MemberData(nameof(GoldenData.Subset), MemberType = typeof(GoldenData))]
    public void Utterances_ShouldNotDependOnHowTheAudioIsBatched(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var engine = models.Get(variant);
        SetLanguage(engine, golden);
        var audio = golden.LoadAudio();
        var n = engine.ChunkSamples;
        using var recognizer = Wrap(engine);

        var aligned = Run(recognizer, TestAudio.Split(audio, n));
        var schedules = new Dictionary<string, IEnumerable<float[]>>
        {
            ["3200 samples"] = TestAudio.Split(audio, 3200),
            ["800 samples"] = TestAudio.Split(audio, 800),
            ["one call"] = [audio],
            ["odd sizes"] = TestAudio.Split(audio, 1, 7, 4999, 20001, 333),
            ["1 to 3 chunks"] = TestAudio.Split(audio, n + 1, 3 * n, 5, 2 * n - 7),
        };
        string Digest(UtteranceRun run) =>
            string.Join('|', run.Finals) + "#" + string.Join(",", run.Words.Select(w => $"{w.Word}@{w.StartSeconds:F6}-{w.EndSeconds:F6}")) + "||" + run.Tail;
        var expected = Digest(aligned);

        foreach (var (label, pieces) in schedules)
        {
            engine.ResetStreamingState();
            using var other = Wrap(engine);
            Assert.True(expected == Digest(Run(other, pieces)), $"{name}: results differ with {label}");
        }
    }

    [NemotronGoldenTheory(vad: true)]
    [MemberData(nameof(GoldenData.Vad), MemberType = typeof(GoldenData))]
    public void WithTheSilenceDrop_TheClockShouldKeepDroppedChunksAndTheTextShouldBeTheSame(string variant, string name)
    {
        var golden = GoldenCase.Get(name);
        var engine = models.Get(variant);
        Assert.True(engine.TrySetVad(true));
        Assert.True(engine.TrySetLanguage(golden.LangId ?? "0"));
        var audio = golden.LoadAudio();
        var n = engine.ChunkSamples;

        var plain = Replay.Run(engine, audio, golden.FeedSamples > 0 ? golden.FeedSamples : n);
        engine.ResetStreamingState();
        using var recognizer = Wrap(engine);
        var run = Run(recognizer, TestAudio.Split(audio, n));

        Assert.Equal(Normalize(plain.Text.ToString()), run.Text);
        AssertWordsAreSane(run.Words, audio.Length / 16000.0, name);
        output.WriteLine($"{name}: {engine.ChunksDropped} chunks dropped, {run.Words.Count} words, last word ends at {(run.Words.Count > 0 ? run.Words[^1].EndSeconds : 0):F2} s of {audio.Length / 16000.0:F2} s");
    }

    // ── offline words ───────────────────────────────────────────────────

    [NemotronModelFact]
    public void TranscribeWords_ShouldEqualTheStreamingWordsAndLeaveTheStreamAlone() => AssertTranscribeWords(autoGain: false);

    [NemotronModelFact]
    public void TranscribeWords_WithAutoGain_ShouldEqualTheStreamingWordsOfANewRecognizer() => AssertTranscribeWords(autoGain: true);

    // TranscribeWords gives what a NEW recognizer's stream reports for the audio, joined like the transcript, whatever the recognizer
    // has streamed (and, with the gain control on, learned) before; the stream that is in progress does not notice the call.
    private static void AssertTranscribeWords(bool autoGain)
    {
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        var clipA = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0002.wav");
        var clipB = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");
        if (autoGain)
        {
            // a quiet clip B (peak 0.01, -40 dBFS): the gain control raises it, so a pass with the wrong gain state hears something else
            var scale = 0.01f / clipB.Max(Math.Abs);
            clipB = clipB.Select(x => x * scale).ToArray();
        }

        NemotronUtteranceRecognizer Create() => new(directory, "cpu", "0", useVad: false,
            new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads }, new NemotronUtteranceOptions { AutoGain = autoGain });

        List<WordTiming> Streamed(float[] clip)
        {
            using var fresh = Create();
            return Run(fresh, TestAudio.Split(clip, fresh.ChunkSamples)).TranscriptWords;
        }
        var expectedA = Streamed(clipA);
        var expectedB = Streamed(clipB);
        Assert.NotEmpty(expectedA);
        Assert.NotEmpty(expectedB);

        using var recognizer = Create();
        var n = recognizer.ChunkSamples;

        // stream clip A, call TranscribeWords in the middle (clip B): the stream must not notice
        var interrupted = new UtteranceRun();
        var pieces = TestAudio.Split(clipA, n).ToList();
        IReadOnlyList<WordTiming>? offlineB = null;
        for (var k = 0; k < pieces.Count; k++)
        {
            interrupted.Results.Add(recognizer.ProcessUtterance(pieces[k]));
            if (k == pieces.Count / 2)
                offlineB = recognizer.TranscribeWords(clipB);
        }
        interrupted.Results.Add(recognizer.FlushUtterance());

        static IEnumerable<(string, double, double)> Key(IEnumerable<WordTiming> words) => words.Select(w => (w.Word, w.StartSeconds, w.EndSeconds));
        Assert.Equal(Key(expectedA), Key(interrupted.TranscriptWords));
        Assert.Equal(Key(expectedB), Key(offlineB!));

        // after the stream (not reset: the second engine) and after a reset (the engine itself): the same words again
        Assert.Equal(Key(expectedA), Key(recognizer.TranscribeWords(clipA)));
        recognizer.ResetStreamingState();
        Assert.Equal(Key(expectedA), Key(recognizer.TranscribeWords(clipA)));
        Assert.Equal(Key(expectedB), Key(recognizer.TranscribeWords(clipB)));
    }

    // ── timing accuracy on the committed truth sample ───────────────────

    private void AssertDialogueSample(string variant, string language, double maxMedianMs, double maxP90Ms, double maxEarlyShare, double minSpeakerAccuracy)
    {
        var truth = TruthSample.Load(language);
        var engine = models.Get(variant);
        Assert.True(engine.TrySetLanguage(language));
        var n = engine.ChunkSamples;

        double[] Measure(NemotronWordTimingOptions timing, out UtteranceRun timedRun)
        {
            engine.ResetStreamingState();
            using var recognizer = Wrap(engine, new NemotronUtteranceOptions { WordTiming = timing });
            timedRun = Run(recognizer, TestAudio.Split(truth.Audio, n));
            AssertWordsAreSane(timedRun.Words, truth.Audio.Length / 16000.0, $"{variant} {language}");
            var rows = truth.Score(timedRun.Words);
            var abs = rows.Select(r => Math.Abs(r.StartError) * 1000).ToList();
            return
            [
                rows.Count, TruthSample.Percentile(abs, 0.5), TruthSample.Percentile(abs, 0.9),
                rows.Count(r => r.StartError < -0.1) / (double)rows.Count,
                rows.Count(r => r.SpeakerCorrect) / (double)rows.Count,
                TruthSample.Percentile(rows.Select(r => Math.Abs(r.EndError) * 1000), 0.5),
            ];
        }

        var calibrated = Measure(NemotronWordTimingOptions.Default, out var run);
        var raw = Measure(NemotronWordTimingOptions.Raw, out _);
        var unsnapped = Measure(NemotronWordTimingOptions.Default with { Snap = null }, out _);
        output.WriteLine($"{variant} {language}: {truth.Words.Count} truth words, {calibrated[0]:F0} matched");
        output.WriteLine($"  raw slot start      : median |e| {raw[1]:F0} ms, p90 {raw[2]:F0}, early {raw[3]:P0}, speaker {raw[4]:P1}, end median {raw[5]:F0}");
        output.WriteLine($"  calibrated          : median |e| {unsnapped[1]:F0} ms, p90 {unsnapped[2]:F0}, early {unsnapped[3]:P0}, speaker {unsnapped[4]:P1}, end median {unsnapped[5]:F0}");
        output.WriteLine($"  calibrated + snapped: median |e| {calibrated[1]:F0} ms, p90 {calibrated[2]:F0}, early {calibrated[3]:P0}, speaker {calibrated[4]:P1}, end median {calibrated[5]:F0}");

        Assert.True(calibrated[0] >= 0.85 * truth.Words.Count, $"only {calibrated[0]} of {truth.Words.Count} words matched");
        Assert.True(calibrated[1] <= maxMedianMs, $"median start error {calibrated[1]:F0} ms");
        Assert.True(calibrated[2] <= maxP90Ms, $"p90 start error {calibrated[2]:F0} ms");
        Assert.True(calibrated[3] <= maxEarlyShare, $"early starts {calibrated[3]:P1}");
        Assert.True(calibrated[4] >= minSpeakerAccuracy, $"speaker accuracy {calibrated[4]:P1}");
        // the raw frame slot is late by 0.2 to 0.3 s: the calibration must be a clear improvement
        Assert.True(raw[1] > 2 * calibrated[1], $"raw median {raw[1]:F0} ms vs calibrated {calibrated[1]:F0} ms");
        Assert.True(run.Results.Count(r => r.Final is not null) >= 2, "the dialogue should end in at least two utterances");
    }

    // The limits sit a little above what the layer measures on the committed sample (this sample is small, 21 to 28 words: one word
    // is 4 to 5 points of the shares): median / p90 of the start error in ms, share of starts earlier than -100 ms, speaker accuracy.
    // Measured: en c056 58 / 148 ms, 0 %, 100 %; en c112 67 / 190, 0 %, 100 %; ru c056 41 / 91, 0 %, 100 %; ru c112 48 / 104, 5 %, 100 %.
    [NemotronModelFact("int4-c056")]
    public void Dialogue_English_Int4C056_ShouldBeTimedAccurately() => AssertDialogueSample("int4-c056", "en", 70, 180, 0.08, 0.98);

    [NemotronModelFact("int4-c056")]
    public void Dialogue_Russian_Int4C056_ShouldBeTimedAccurately() => AssertDialogueSample("int4-c056", "ru", 55, 115, 0.08, 0.98);

    [NemotronModelFact("int4-c112")]
    public void Dialogue_English_Int4C112_ShouldBeTimedAccurately() => AssertDialogueSample("int4-c112", "en", 80, 220, 0.08, 0.98);

    [NemotronModelFact("int4-c112")]
    public void Dialogue_Russian_Int4C112_ShouldBeTimedAccurately() => AssertDialogueSample("int4-c112", "ru", 60, 125, 0.12, 0.98);
}
