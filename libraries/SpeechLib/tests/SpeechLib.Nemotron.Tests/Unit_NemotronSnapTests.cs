using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>The energy history, the speech mask and the snapping of word starts and ends to the speech around pauses.</summary>
public sealed class Unit_NemotronSnapTests
{
    private const int N = 8960;
    private const int Rate = 16000;

    private static readonly Lazy<NemotronVocabulary> VocabularyOf = new(() =>
        NemotronVocabulary.FromPieces(NemotronTestEnvironment.ReadVocabularyLines(), 13087, 13088));

    private static int Id(string piece) => Array.IndexOf(NemotronTestEnvironment.ReadVocabularyLines(), piece);

    /// <summary>Room tone of about -68 dBFS (uniform +-0.0004), like the truth fixtures.</summary>
    private static float[] Noise(double seconds, int seed = 1, double amplitude = 0.0004)
    {
        var random = new Random(seed);
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        return samples;
    }

    private static float[] Tone(double seconds, double amplitude = 0.1, double frequency = 220)
    {
        var samples = new float[(int)(seconds * Rate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / Rate));
        return samples;
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private static NemotronEnergyTracker Track(float[] audio)
    {
        var tracker = new NemotronEnergyTracker(60, Rate);
        tracker.Append(audio);
        return tracker;
    }

    // ── tracker and mask ────────────────────────────────────────────────

    [Fact]
    public void Tracker_ShouldCountCompleteTenMillisecondFramesAcrossAppendBoundaries()
    {
        var tracker = new NemotronEnergyTracker(10, Rate);

        tracker.Append(new float[100]);
        Assert.Equal(0, tracker.FrameCount);
        tracker.Append(new float[100]);
        Assert.Equal(1, tracker.FrameCount);
        tracker.Append(new float[160 * 5]);
        Assert.Equal(6, tracker.FrameCount);                // 1000 samples = 6 frames + 40 left over
    }

    [Fact]
    public void Mask_OfSpeechBetweenRoomTone_ShouldFindTheOnsetAndTheOffset()
    {
        var tracker = Track(Concat(Noise(1.0), Tone(0.5), Noise(1.0, seed: 2)));

        var mask = tracker.Snapshot(new NemotronSnapOptions());

        Assert.NotNull(mask);
        var onset = mask!.FindOnset(0.9, 0.03, 0.35, 10);
        Assert.NotNull(onset);
        Assert.Equal(1.0, onset!.Value.Seconds, 2);
        Assert.Equal(0.0, onset.Value.SilenceStartFrame * 0.01, 2);       // the silence before it began at the start of the stream
        Assert.Equal(1.5, mask.FindOffset(1.4, 0.35, 0.35, 10)!.Value, 2);
    }

    [Fact]
    public void Mask_Onset_ShouldRespectTheWindowAndTheSilenceLength()
    {
        // 80 ms of silence before the second burst: shorter than 10 frames, so it is not a pause
        var tracker = Track(Concat(Noise(1.0), Tone(0.3), Noise(0.08, seed: 3), Tone(0.3), Noise(1.0, seed: 2)));
        var mask = tracker.Snapshot(new NemotronSnapOptions())!;

        Assert.Equal(1.0, mask.FindOnset(1.0, 0.03, 0.35, 10)!.Value.Seconds, 2);
        Assert.Null(mask.FindOnset(1.38, 0.03, 0.05, 10));          // the burst at 1.38 follows only 8 silent frames
        Assert.Equal(1.38, mask.FindOnset(1.38, 0.03, 0.05, 6)!.Value.Seconds, 2);
        Assert.Null(mask.FindOnset(2.5, 0.03, 0.35, 10));           // nothing near
        Assert.Null(mask.FindOnset(1.1, 0.0, 0.05, 10));            // the onset at 1.0 lies before the window
    }

    [Fact]
    public void Mask_WithoutADynamicRange_ShouldNotExist()
    {
        Assert.Null(Track(Noise(3.0)).Snapshot(new NemotronSnapOptions()));                             // only room tone
        Assert.Null(Track(Tone(3.0)).Snapshot(new NemotronSnapOptions()));                              // only speech
        Assert.Null(Track(Concat(Noise(1.0, amplitude: 0.05), Tone(0.5, 0.08), Noise(1.0, 2, 0.05))).Snapshot(new NemotronSnapOptions())); // a floor of -30 dBFS
        Assert.Null(Track(new float[100]).Snapshot(new NemotronSnapOptions()));                         // less than 20 frames
    }

    [Theory]
    [InlineData(24, true)]     // noise 24 dB below the speech frames: a mask
    [InlineData(22, true)]
    [InlineData(18, false)]    // 18 dB: speech and noise overlap too much, nothing is snapped
    [InlineData(14, false)]
    public void Mask_ShouldExistOnlyWhenTheSpeechIsLoudEnoughAboveTheNoise(double dbAboveNoise, bool exists)
    {
        // the floor is a level difference, not an absolute level: the same difference at -30 and at -60 dBFS decides the same
        foreach (var noiseAmplitude in new[] { 0.03, 0.0005 })
        {
            // uniform noise has an RMS of a / sqrt(3), a sine of b / sqrt(2)
            var toneAmplitude = noiseAmplitude / Math.Sqrt(3) * Math.Pow(10, dbAboveNoise / 20) * Math.Sqrt(2);
            var tracker = Track(Concat(Noise(1.0, 1, noiseAmplitude), Tone(0.5, toneAmplitude), Noise(1.0, 2, noiseAmplitude)));

            var mask = tracker.Snapshot(new NemotronSnapOptions());

            Assert.Equal(exists, mask is not null);
        }
        Assert.NotNull(Track(Concat(Noise(1.0, 1, 0.03), Tone(0.5, 0.03 / Math.Sqrt(3) * Math.Pow(10, 14 / 20.0) * Math.Sqrt(2)), Noise(1.0, 2, 0.03)))
            .Snapshot(new NemotronSnapOptions { MinSpeechToNoiseDb = 12 }));
    }

    [Fact]
    public void Mask_OfSparseSpeech_ShouldExistWhenTheSpeechIsLessThanAFifthOfTheHistory()
    {
        // one word of 0.5 s after half a minute of room tone: 1.6 % of the frames. The 95th percentile of all frames is room tone
        // there; the speech level is taken from the frames that clearly stand out of it
        var tracker = Track(Concat(Noise(30.0), Tone(0.5), Noise(2.0, seed: 2)));

        var mask = tracker.Snapshot(new NemotronSnapOptions());

        Assert.NotNull(mask);
        Assert.Equal(30.0, mask!.FindOnset(29.95, 0.03, 0.35, 20)!.Value.Seconds, 2);
        Assert.Equal(30.5, mask.FindOffset(30.4, 0.35, 0.35, 20)!.Value, 2);
    }

    [Fact]
    public void Mask_OfRoomToneWithAFewShortClicks_ShouldNotSnapAnything()
    {
        // 25 clicks of 20 ms in a minute of room tone: they stand out of the floor, but none is a stretch of speech
        var audio = Noise(60.0);
        for (var k = 0; k < 25; k++)
            for (var i = 0; i < 320; i++)
                audio[(20 + 23 * k) * 1600 + i] = 0.2f * (i % 2 == 0 ? 1 : -1);
        var mask = Track(audio).Snapshot(new NemotronSnapOptions());

        Assert.True(mask is null || mask.FindOnset(10.0, 0.03, 20.0, 20) is null);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public void Snap_OfTheFirstWordAfterHalfAMinuteOfRoomTone_ShouldMoveItOntoTheOnsetWhateverSpeechCameBefore(double earlierSpeechSeconds)
    {
        // a dictation: one sentence now and then. The word starts at 30.0 s; its calibrated start (frame 377 -> 29.951 s) is early
        var audio = Concat(Noise(5.0), Tone(earlierSpeechSeconds), Noise(30.0 - 5.0 - earlierSpeechSeconds, seed: 2), Tone(0.5), Noise(1.5, seed: 3));
        using var recognizer = Make(new NemotronUtteranceOptions(), (377, "▁the"));

        var word = Assert.Single(PartialWordsAfter(recognizer, audio.Take(54 * N).ToArray()));          // the word is in the last chunk, so it is still open

        Assert.Equal(30.0, word.StartSeconds, 2);
    }

    [Fact]
    public void Tracker_ShouldForgetAudioOlderThanItsHistory()
    {
        var tracker = new NemotronEnergyTracker(2, Rate);        // two seconds
        tracker.Append(Concat(Noise(1.0), Tone(0.5), Noise(3.0, seed: 2)));

        Assert.Equal(250, tracker.FirstFrame);
        var mask = tracker.Snapshot(new NemotronSnapOptions());
        Assert.Null(mask);                                       // only room tone is left in the ring
    }

    [Fact]
    public void Tracker_Reset_ShouldStartOver()
    {
        var tracker = Track(Concat(Noise(1.0), Tone(0.5), Noise(1.0, seed: 2)));
        tracker.Reset();

        Assert.Equal(0, tracker.FrameCount);
        Assert.Null(tracker.Snapshot(new NemotronSnapOptions()));
    }

    // ── snapping of words ───────────────────────────────────────────────

    private static NemotronUtteranceRecognizer Make(NemotronUtteranceOptions options, params (int Frame, string Piece)[] tokens)
    {
        var engine = new FakeChunkEngine(N, 7)
        {
            Script = (chunk, _) => tokens.Where(t => t.Frame / 7 == chunk).Select(t => (Id(t.Piece), t.Frame % 7)),
        };
        var inner = new NemotronRnntRecognizer(engine, NemotronTestEnvironment.LoadConfig("int4-c056"), VocabularyOf.Value, () => new FakeGate(N, _ => true));
        return new NemotronUtteranceRecognizer(inner, options);
    }

    private static List<SpeechLib.Models.WordTiming> PartialWordsAfter(NemotronUtteranceRecognizer recognizer, float[] audio)
    {
        SpeechLib.StreamingResult result = default;
        foreach (var piece in TestAudio.Split(audio, N))
            result = recognizer.ProcessUtterance(piece);
        return result.PartialWords!.ToList();
    }

    // word "princip" in frames 13..15: calibrated start 13 * 0.08 - 0.224 + 0.015 = 0.831 s, calibrated end 16 * 0.08 - 0.144 = 1.136 s
    private static readonly (int, string)[] Word = [(13, "▁prin"), (14, "ci"), (15, "p")];

    [Fact]
    public void Snap_ShouldMoveAWordThatFollowsAPauseOntoTheSpeechOnsetAndItsEndOntoTheOffset()
    {
        var audio = Concat(Noise(1.0), Tone(0.3), Noise(3 * N / (double)Rate - 1.3 + 0.01, seed: 2)).Take(3 * N).ToArray();
        using var snapped = Make(new NemotronUtteranceOptions(), Word);
        using var calibrated = Make(new NemotronUtteranceOptions { WordTiming = NemotronWordTimingOptions.Default with { Snap = null } }, Word);

        var withSnap = Assert.Single(PartialWordsAfter(snapped, audio));
        var without = Assert.Single(PartialWordsAfter(calibrated, audio));

        Assert.Equal(0.831, without.StartSeconds, 3);
        Assert.Equal(1.136, without.EndSeconds, 3);
        Assert.Equal(1.0, withSnap.StartSeconds, 2);            // the onset of the tone
        Assert.Equal(1.3, withSnap.EndSeconds, 2);              // its offset
        Assert.Equal(withSnap.StartSeconds, withSnap.Tokens![0].StartSeconds);
    }

    [Fact]
    public void Snap_ShouldNotMoveTheEndOfTheLastWordFartherForwardThanTheLimit()
    {
        // the speech goes on until 1.6 s: 0.46 s after the calibrated end (1.136 s), which may be the end of words not emitted yet
        var audio = Concat(Noise(1.0), Tone(0.6), Noise(4 * N / (double)Rate - 1.6 + 0.01, seed: 2)).Take(4 * N).ToArray();
        using var limited = Make(new NemotronUtteranceOptions(), Word);
        using var wide = Make(new NemotronUtteranceOptions { WordTiming = NemotronWordTimingOptions.Default with { Snap = new NemotronSnapOptions { LastEndMaxForwardSeconds = 0.5 } } }, Word);

        var narrow = Assert.Single(PartialWordsAfter(limited, audio));
        var far = Assert.Single(PartialWordsAfter(wide, audio));

        Assert.Equal(1.0, narrow.StartSeconds, 2);
        Assert.Equal(1.136, narrow.EndSeconds, 3);
        Assert.Equal(1.6, far.EndSeconds, 2);
    }

    [Fact]
    public void Snap_ShouldNotMoveAWordBackwardOrBeyondTheForwardWindow()
    {
        using var early = Make(new NemotronUtteranceOptions(), Word);        // onset at 0.60 s: 0.23 s before the calibrated start
        using var late = Make(new NemotronUtteranceOptions(), Word);         // onset at 1.60 s: 0.77 s after it
        var total = 3 * N / (double)Rate;

        var before = Assert.Single(PartialWordsAfter(early, Concat(Noise(0.6), Tone(0.3), Noise(total - 0.9, seed: 2))));
        var after = Assert.Single(PartialWordsAfter(late, Concat(Noise(1.6), Tone(0.05, 0.1), Noise(total - 1.65, seed: 2))));

        Assert.Equal(0.831, before.StartSeconds, 3);
        Assert.Equal(0.831, after.StartSeconds, 3);
    }

    [Fact]
    public void Snap_WithoutAPause_ShouldLeaveTheCalibratedTimes()
    {
        using var recognizer = Make(new NemotronUtteranceOptions(), Word);

        var word = Assert.Single(PartialWordsAfter(recognizer, Tone(3 * N / (double)Rate)));   // speech all the time: no mask

        Assert.Equal(0.831, word.StartSeconds, 3);
        Assert.Equal(1.136, word.EndSeconds, 3);
    }

    [Fact]
    public void Snap_ShouldKeepWordsOrderedAndNeverOverlapped()
    {
        // two words with a pause between them; both snap, the first word's end goes to the offset before the pause
        (int, string)[] tokens = [(13, "▁the"), (22, "▁and")];
        var audio = Concat(Noise(1.0), Tone(0.3), Noise(0.5, seed: 2), Tone(0.3, frequency: 300), Noise(0.6, seed: 4));   // speech 1.0-1.3 and 1.8-2.1
        using var recognizer = Make(new NemotronUtteranceOptions(), tokens);

        var words = PartialWordsAfter(recognizer, audio.Concat(Noise(0.1, 5)).Take(4 * N).ToArray());

        Assert.Equal(2, words.Count);
        Assert.Equal(1.0, words[0].StartSeconds, 2);
        Assert.Equal(1.3, words[0].EndSeconds, 2);
        Assert.Equal(1.8, words[1].StartSeconds, 2);
        Assert.True(words[0].EndSeconds <= words[1].StartSeconds);
    }

    [Fact]
    public void Snap_OfSeveralWordsEmittedRightAfterAPause_ShouldMoveTheFirstOntoTheOnsetAndKeepTheOthersOrderedBehindIt()
    {
        // three words in consecutive frames after a pause: the model showed them together, one burst of speech starts at 5.0 s.
        // Calibrated starts 4.911 / 4.991 / 5.071 s; the onset is 0.089 s later than the first, and the others moved with it
        (int, string)[] tokens = [(64, "▁the"), (65, "▁there"), (66, "▁and")];
        var audio = Concat(Noise(5.0), Tone(0.9), Noise(0.26, seed: 2));
        using var recognizer = Make(new NemotronUtteranceOptions(), tokens);

        var words = PartialWordsAfter(recognizer, audio);

        Assert.Equal(3, words.Count);
        Assert.Equal(5.000, words[0].StartSeconds, 2);                  // the onset
        Assert.Equal(5.080, words[1].StartSeconds, 2);                  // 0.08 s behind it: one frame later, as the model emitted them
        Assert.Equal(5.160, words[2].StartSeconds, 2);
        AssertStrictlyOrdered(words);
        Assert.Equal(words[1].StartSeconds, words[0].EndSeconds, 6);    // words that follow each other without a gap share the border
        Assert.Equal(words[2].StartSeconds, words[1].EndSeconds, 6);
    }

    [Fact]
    public void Snap_OfWordsEmittedInTheSameFrame_ShouldSeparateThemByTheMinimumWordLength()
    {
        // the snapped start of the first word is the same onset the second word finds: it may not start where the first starts
        (int, string)[] tokens = [(64, "▁the"), (64, "▁there")];
        var audio = Concat(Noise(5.0), Tone(0.9), Noise(0.26, seed: 2));
        using var recognizer = Make(new NemotronUtteranceOptions(), tokens);

        var words = PartialWordsAfter(recognizer, audio);

        Assert.Equal(2, words.Count);
        Assert.Equal(5.000, words[0].StartSeconds, 2);
        AssertStrictlyOrdered(words);
        Assert.Equal(new NemotronSnapOptions().MinWordSeconds, words[1].StartSeconds - words[0].StartSeconds, 6);
    }

    [Fact]
    public void Snap_ShouldNotMoveALaterWordOfTheBurstEarlierThanItsCalibratedStart()
    {
        // the onset (4.90 s) lies 0.011 s BEFORE the calibrated start of the first word: that word may move back a little, the
        // word behind it must not follow it (early is what hurts)
        (int, string)[] tokens = [(64, "▁the"), (64, "▁there")];
        var audio = Concat(Noise(4.9), Tone(1.0), Noise(0.26, seed: 2));
        using var recognizer = Make(new NemotronUtteranceOptions(), tokens);

        var words = PartialWordsAfter(recognizer, audio);

        Assert.Equal(4.90, words[0].StartSeconds, 2);
        Assert.True(words[1].StartSeconds >= 4.911 - 1e-9);
        AssertStrictlyOrdered(words);
    }

    private static void AssertStrictlyOrdered(IReadOnlyList<SpeechLib.Models.WordTiming> words)
    {
        var minimum = new NemotronSnapOptions().MinWordSeconds;
        for (var i = 0; i < words.Count; i++)
        {
            Assert.True(words[i].EndSeconds - words[i].StartSeconds >= minimum - 1e-9, $"{words[i].Word} is {words[i].EndSeconds - words[i].StartSeconds:F3} s long");
            if (i > 0)
            {
                Assert.True(words[i].StartSeconds - words[i - 1].StartSeconds >= minimum - 1e-9, $"{words[i].Word} starts {words[i].StartSeconds - words[i - 1].StartSeconds:F3} s after {words[i - 1].Word}");
                Assert.True(words[i].StartSeconds >= words[i - 1].EndSeconds - 1e-9, $"{words[i].Word} overlaps {words[i - 1].Word}");
            }
        }
    }

    [Fact]
    public void Snap_Disabled_ShouldNotCreateAnEnergyTracker()
    {
        using var recognizer = Make(new NemotronUtteranceOptions { WordTiming = NemotronWordTimingOptions.Raw }, Word);

        var word = Assert.Single(PartialWordsAfter(recognizer, Concat(Noise(1.0), Tone(0.4), Noise(0.3, seed: 2))));

        Assert.Equal(13 * 0.08, word.StartSeconds, 6);          // the raw frame slot
        Assert.Equal(16 * 0.08, word.EndSeconds, 6);
    }
}
