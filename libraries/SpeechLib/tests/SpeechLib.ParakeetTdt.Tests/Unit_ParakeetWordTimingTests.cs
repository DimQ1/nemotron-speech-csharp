using SpeechLib.Models;
using SpeechLib.ParakeetTdt;
using Xunit;

namespace SpeechLib.ParakeetTdt.Tests;

/// <summary>
/// Token / word timings of the decoder (fake decoder output: token ids with encoder frames),
/// without the ONNX model: positions on the absolute timeline, word grouping and the energy refiner.
/// </summary>
public sealed class Unit_ParakeetWordTimingTests
{
    private const int Frame = 1280; // samples per encoder frame (80 ms at 16 kHz)
    private const int Rate = 16000;

    // Fake vocabulary: id -> piece ('▁' marks a word start, rendered as a leading space like the recognizer does).
    private static readonly Dictionary<int, string> Vocab = new()
    {
        [1] = " he", [2] = "llo", [3] = " wor", [4] = "ld", [5] = ".", [6] = " .", [7] = " it", [8] = ",", [9] = "",
    };
    private static readonly HashSet<int> WordStarts = [1, 3, 6, 7];

    private static List<WordTiming> Words(params TimedWordBuilder.RawToken[] tokens) =>
        TimedWordBuilder.Build(tokens, Rate, WordStarts.Contains, id => Vocab.GetValueOrDefault(id, ""));

    [Fact]
    public void PositionToken_ShouldBeAbsoluteAndAtLeastOneFrameLong()
    {
        var t = ParakeetTdtRecognizer.PositionToken(3, windowStartSample: 10 * Frame, emitFrame: 4, nextFrame: 4);
        Assert.Equal(14L * Frame, t.StartSample);
        Assert.Equal(15L * Frame, t.EndSample); // zero duration still covers its frame

        var d = ParakeetTdtRecognizer.PositionToken(3, 10 * Frame, 4, 7);
        Assert.Equal(17L * Frame, d.EndSample);
    }

    [Fact]
    public void PositionToken_ShouldGiveTheSameTimeForTheSameFrameInDifferentWindows()
    {
        // Step A encodes a window that starts at sample 0, step B (after a trim) one that starts 20 frames later:
        // frame 25 of A and frame 5 of B are the same encoder frame of the stream.
        var a = ParakeetTdtRecognizer.PositionToken(1, 0, 25, 27);
        var b = ParakeetTdtRecognizer.PositionToken(1, 20L * Frame, 5, 7);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Build_ShouldGroupTokensIntoWordsAtWordStartMarkers()
    {
        var words = Words(
            new(1, 0 * Frame, 1 * Frame),   // " he"
            new(2, 1 * Frame, 2 * Frame),   // "llo"
            new(3, 3 * Frame, 4 * Frame),   // " wor"
            new(4, 4 * Frame, 6 * Frame));  // "ld"

        Assert.Equal(["hello", "world"], words.Select(w => w.Word));
        Assert.Equal(0.0, words[0].StartSeconds);
        Assert.Equal(2.0 * Frame / Rate, words[0].EndSeconds, 6);
        Assert.Equal(3.0 * Frame / Rate, words[1].StartSeconds, 6);
        Assert.Equal(6.0 * Frame / Rate, words[1].EndSeconds, 6);
        Assert.Equal(2, words[0].Tokens!.Count);
        Assert.Equal("llo", words[0].Tokens![1].Text);
    }

    [Fact]
    public void Build_ShouldAttachPunctuationToThePreviousWord()
    {
        var words = Words(
            new(1, 0, Frame), new(2, Frame, 2 * Frame),
            new(5, 2 * Frame, 3 * Frame),   // "." continuing the word
            new(6, 4 * Frame, 5 * Frame),   // " ." a detached mark
            new(7, 6 * Frame, 7 * Frame));  // " it"

        Assert.Equal(["hello..", "it"], words.Select(w => w.Word));
        // Marks stay in the token list but do not stretch the word over the pause they were emitted in.
        Assert.Equal(2.0 * Frame / Rate, words[0].EndSeconds, 6);
        Assert.Equal(4, words[0].Tokens!.Count);
    }

    [Fact]
    public void Build_ShouldKeepWordsOrderedAndNotOverlapping()
    {
        // A long duration of the first word runs into the second: its end is cut at the next start.
        var words = Words(new(1, 0, 6 * Frame), new(7, 2 * Frame, 3 * Frame));
        Assert.Equal(2.0 * Frame / Rate, words[0].EndSeconds, 6);
        Assert.True(words[0].EndSeconds <= words[1].StartSeconds);
    }

    [Fact]
    public void Build_ShouldSkipTokensWithoutText()
    {
        var words = Words(new(9, 0, Frame), new(1, Frame, 2 * Frame));
        Assert.Equal(["he"], words.Select(w => w.Word));
    }

    // ── energy refiner ────────────────────────────────────────────────────────

    private static float[] Speech(double totalSeconds, params (double From, double To)[] voiced)
    {
        var rng = new Random(3);
        var samples = new float[(int)(totalSeconds * Rate)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)((rng.NextDouble() - 0.5) * 0.0004);            // room tone
        foreach (var (from, to) in voiced)
            for (var i = (int)(from * Rate); i < Math.Min(samples.Length, (int)(to * Rate)); i++)
                samples[i] += (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / Rate));
        return samples;
    }

    private static List<TimedWordBuilder.RawToken> Refine(float[] audio, long audioStart, params TimedWordBuilder.RawToken[] tokens) =>
        TokenTimingRefiner.Refine(tokens, WordStarts.Contains, audio, audioStart);

    [Fact]
    public void Refine_ShouldMoveAWordEmittedInSilenceToTheSpeechOnset()
    {
        // Speech from 1.0 s; the decoder emitted the word 0.3 s early.
        var audio = Speech(3.0, (1.0, 2.0));
        var token = new TimedWordBuilder.RawToken(1, (long)(0.7 * Rate), (long)(1.1 * Rate));

        var refined = Refine(audio, 0, token)[0];

        Assert.InRange(refined.StartSample / (double)Rate, 0.99, 1.02);
        Assert.True(refined.EndSample > refined.StartSample);
    }

    [Fact]
    public void Refine_ShouldCutATokenEndAtTheLastVoicedFrame()
    {
        // The duration of the last word runs 0.8 s into the silence after the speech.
        var audio = Speech(3.0, (1.0, 1.5));
        var token = new TimedWordBuilder.RawToken(1, (long)(1.0 * Rate), (long)(2.3 * Rate));

        var refined = Refine(audio, 0, token)[0];

        Assert.InRange(refined.EndSample / (double)Rate, 1.45, 1.6);
    }

    [Fact]
    public void Refine_ShouldMoveALateWordStartBackToTheStartOfItsVoicedRunAfterAPause()
    {
        // "he" 0.5-0.9 s, pause, "wor" really starts at 1.2 s but was emitted at 1.45 s.
        var audio = Speech(3.0, (0.5, 0.9), (1.2, 2.0));
        var tokens = new[]
        {
            new TimedWordBuilder.RawToken(1, (long)(0.5 * Rate), (long)(0.9 * Rate)),
            new TimedWordBuilder.RawToken(3, (long)(1.45 * Rate), (long)(2.0 * Rate)),
        };

        var refined = Refine(audio, 0, tokens);

        Assert.InRange(refined[1].StartSample / (double)Rate, 1.19, 1.22);
        Assert.Equal(tokens[0].StartSample, refined[0].StartSample);
    }

    [Fact]
    public void Refine_ShouldNotMoveAWordInsideContinuousSpeech()
    {
        var audio = Speech(3.0, (0.5, 2.0));
        var tokens = new[]
        {
            new TimedWordBuilder.RawToken(1, (long)(0.5 * Rate), (long)(1.0 * Rate)),
            new TimedWordBuilder.RawToken(3, (long)(1.2 * Rate), (long)(1.8 * Rate)),
        };

        var refined = Refine(audio, 0, tokens);

        Assert.Equal(tokens[1].StartSample, refined[1].StartSample);
    }

    [Fact]
    public void Refine_ShouldLeaveTokensAloneWithoutDynamicRange()
    {
        var rng = new Random(1);
        var noise = new float[3 * Rate];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (float)((rng.NextDouble() - 0.5) * 0.01);
        var token = new TimedWordBuilder.RawToken(1, Rate, 2 * Rate);

        Assert.Equal(token, Refine(noise, 0, token)[0]);
    }

    [Fact]
    public void Refine_ShouldUseTheAbsolutePositionOfTheAudio()
    {
        // The same scene 100 s into the stream.
        const long offset = 100L * Rate;
        var audio = Speech(3.0, (1.0, 2.0));
        var token = new TimedWordBuilder.RawToken(1, offset + (long)(0.7 * Rate), offset + (long)(1.1 * Rate));

        var refined = Refine(audio, offset, token)[0];

        Assert.InRange((refined.StartSample - offset) / (double)Rate, 0.99, 1.02);
    }

    [Fact]
    public void Refine_ShouldKeepTokenStartsOrdered()
    {
        var audio = Speech(3.0, (1.0, 2.0));
        var tokens = new[]
        {
            new TimedWordBuilder.RawToken(1, (long)(0.7 * Rate), (long)(0.8 * Rate)),
            new TimedWordBuilder.RawToken(2, (long)(0.8 * Rate), (long)(0.9 * Rate)),   // moves behind its word start
        };

        var refined = Refine(audio, 0, tokens);

        Assert.True(refined[1].StartSample >= refined[0].StartSample);
    }
}
