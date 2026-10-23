using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_AutomaticGainControlTests
{
    private const int Rate = 16000;
    private const int SyllablePeriod = 4800;        // a syllable every 300 ms...
    private const int SyllableLength = 2880;        // ...180 ms long, the fourth slot of every word is a pause

    private static float Db(double dbfs) => (float)Math.Pow(10, dbfs / 20);

    /// <summary>
    /// Speech-like input: 180 ms syllables (two harmonics, 15 ms ramps) every 300 ms, three to a word, a pause slot after each word.
    /// The level moves by tens of dB within a third of a second, as speech does; <paramref name="syllableRms"/> is the RMS of a syllable.
    /// </summary>
    private static float[] Syllables(double seconds, float syllableRms)
    {
        var x = new float[(int)(seconds * Rate)];
        for (var start = 0; start < x.Length; start += SyllablePeriod)
        {
            if (start / SyllablePeriod % 4 == 3)
                continue;
            for (var i = 0; i < SyllableLength && start + i < x.Length; i++)
            {
                var ramp = Math.Min(1.0, Math.Min(i, SyllableLength - i) / 240.0);
                var t = (double)(start + i) / Rate;
                x[start + i] = (float)(syllableRms * Math.Sqrt(2) * ramp * (0.8 * Math.Sin(2 * Math.PI * 180 * t) + 0.6 * Math.Sin(2 * Math.PI * 360 * t)));
            }
        }
        return x;
    }

    /// <summary>Gaussian room tone of the given RMS (fixed seed).</summary>
    private static float[] RoomTone(double seconds, float rms, int seed = 1)
    {
        var random = new Random(seed);
        var tone = new float[(int)(seconds * Rate)];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = (float)(rms * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble()));
        return tone;
    }

    private static float[] Mix(float[] a, float[] b) => a.Select((x, i) => x + (i < b.Length ? b[i] : 0f)).ToArray();

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private static float[] Sine(float amplitude, double seconds) =>
        Enumerable.Range(0, (int)(seconds * Rate)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * 220 * i / Rate))).ToArray();

    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>RMS of the steady middle of syllable <paramref name="index"/> (counted in syllable slots from <paramref name="offset"/>).</summary>
    private static float SyllableRms(float[] audio, int index, int offset = 0) =>
        Rms(audio.AsSpan(offset + index * SyllablePeriod + 480, SyllableLength - 960));

    // ── quiet speech is raised ──────────────────────────────────────────

    [Fact]
    public void Process_QuietSpeechInAQuietRoom_ShouldBoostItTowardTheTarget()
    {
        var agc = new AutomaticGainControl();
        var output = agc.Process(Mix(Syllables(4, Db(-46)), RoomTone(4, Db(-80))));

        Assert.True(agc.HasHeardSpeech);
        Assert.InRange(SyllableRms(output, 12), AutomaticGainControl.TargetRms * 0.8f, AutomaticGainControl.TargetRms * 1.2f);
    }

    [Fact]
    public void Process_LoudSpeech_ShouldPassUnchanged()
    {
        var agc = new AutomaticGainControl();
        var input = Mix(Syllables(3, Db(-18)), RoomTone(3, Db(-66)));
        var output = agc.Process(input);

        Assert.True(agc.HasHeardSpeech);
        Assert.Equal(1f, agc.Gain);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Process_ShouldLimitGain()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Mix(Syllables(4, Db(-60)), RoomTone(4, Db(-95))));

        Assert.Equal(AutomaticGainControl.MaxGain, agc.Gain);
    }

    [Fact]
    public void Process_FirstSyllable_ShouldBeRaisedFromItsOnsetAndKeepItsBoost()
    {
        // the model drops an utterance whose level jumps after its first word, so the first syllable is not left at its quiet level
        var agc = new AutomaticGainControl();
        var lead = RoomTone(1, Db(-80));
        var input = Concat(lead, Mix(Syllables(2, Db(-46)), RoomTone(2, Db(-80), seed: 2)));
        var output = agc.Process(input);

        Assert.Equal(lead, output[..lead.Length]);                                      // the room before it as it came
        Assert.True(SyllableRms(output, 0, lead.Length) > 5 * SyllableRms(input, 0, lead.Length), "the first syllable should be raised");
        Assert.True(agc.HasHeardSpeech);
        Assert.InRange(SyllableRms(output, 1, lead.Length) / SyllableRms(output, 0, lead.Length), 0.7f, 1.4f);   // and the next one alike
    }

    // ── room tone is not speech (G1) ────────────────────────────────────

    [Theory]
    [InlineData(-66)]
    [InlineData(-60)]
    [InlineData(-54)]
    [InlineData(-48)]
    [InlineData(-42)]
    public void Process_RoomToneAlone_ShouldNeverBeBoosted(double dbfs)
    {
        // the rule this replaced took every frame above -60 dBFS for speech and lifted a -54 dBFS tone by 26 dB
        var agc = new AutomaticGainControl();
        var input = RoomTone(10, Db(dbfs));
        var output = agc.Process(input);

        Assert.False(agc.HasHeardSpeech);
        Assert.Equal(1f, agc.Gain);
        Assert.Equal(input, output);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(-54)]
    [InlineData(-48)]
    public void Process_QuietSpeechAfterARoomTone_ShouldNotLiftTheToneAboveTheCeiling(double toneDbfs)
    {
        // a stream that starts in a room tone, then a quiet talker 20 dB above it, then a pause: the talker is raised as far as the
        // ceiling allows and the tone never comes out louder than the ceiling (a room that is louder than the ceiling is not raised)
        var agc = new AutomaticGainControl();
        var lead = RoomTone(6, Db(toneDbfs));
        var speech = Mix(Syllables(3, Db(toneDbfs + 20)), RoomTone(3, Db(toneDbfs), seed: 2));
        var pause = RoomTone(3, Db(toneDbfs), seed: 3);
        var output = agc.Process(Concat(lead, speech, pause));

        Assert.Equal(lead, output[..lead.Length]);                                      // nothing heard yet: unchanged
        Assert.True(agc.HasHeardSpeech);
        var pauseOut = output.AsSpan(lead.Length + speech.Length);
        Assert.True(Rms(pauseOut) <= Math.Max(AutomaticGainControlOptions.Default.NoiseCeilingRms, Rms(pause)) * 1.25f,
            $"the tone of the pause came out at {20 * Math.Log10(Rms(pauseOut)):F1} dBFS");
    }

    [Fact]
    public void Process_PauseAfterSpeech_ShouldKeepTheGainInsteadOfPumping()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Mix(Syllables(3, Db(-46)), RoomTone(3, Db(-74))));
        var gain = agc.Gain;
        var pause = RoomTone(5, Db(-74), seed: 2);
        var output = agc.Process(pause);

        Assert.True(gain > 5, $"quiet speech should have been raised, gain {gain}");
        Assert.Equal(gain, agc.Gain);                                                   // a pause neither raises nor lowers it
        Assert.Equal(pause.Select(s => s * gain), output);
    }

    [Fact]
    public void Process_ClicksAndTyping_ShouldNotBeTakenForSpeech()
    {
        // 5 ms clicks every 150 ms (typing) in a quiet room: each one is far above the floor, none lasts the three frames a boost on
        // probation waits for, none is a syllable
        var agc = new AutomaticGainControl();
        var input = RoomTone(8, Db(-70));
        for (var start = 8000; start + 80 < input.Length; start += 2400)
            for (var i = 0; i < 80; i++)
                input[start + i] += 0.05f * (float)Math.Sin(2 * Math.PI * 2000 * i / Rate) * (1 - i / 80f);
        var output = agc.Process(input);

        Assert.False(agc.HasHeardSpeech);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Process_LoudWordWithAQuietOnset_ShouldNotBeRaised()
    {
        // a breath 35 dB under the word in front of it: the first frame alone would ask for the full boost and clip the word (the rule
        // this replaced did), the loudest of the first three frames does not
        var agc = new AutomaticGainControl();
        var breath = Concat(RoomTone(1, Db(-80)), RoomTone(0.02, Db(-55), seed: 2));
        var input = Concat(breath, Mix(Syllables(2, Db(-18)), RoomTone(2, Db(-80), seed: 3)));
        var output = agc.Process(input);

        Assert.True(agc.HasHeardSpeech);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Process_ASteadySoundThatStartsLater_ShouldNotBeTakenForSpeech()
    {
        // a fan switched on (a step of 20 dB that the floor needs two seconds to follow) or a steady tone: the step starts a burst
        // whose boost is taken back after half a second because it never dips, and nothing boosts after that
        var agc = new AutomaticGainControl();
        var before = RoomTone(3, Db(-75));
        var input = Concat(before, RoomTone(6, Db(-55), seed: 2), Mix(Sine(0.003f, 3), RoomTone(3, Db(-55), seed: 3)));
        var output = agc.Process(input);

        Assert.False(agc.HasHeardSpeech);
        Assert.Equal(1f, agc.Gain);
        var settled = before.Length + Rate * 6 / 10;
        Assert.Equal(input[..before.Length], output[..before.Length]);
        Assert.Equal(input[settled..], output[settled..]);                              // from 0.6 s after the step on: unchanged
    }

    [Fact]
    public void Process_ARoomThatGetsLouderAfterSpeech_ShouldLowerTheGainToTheCeilingWithinTwoSeconds()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Mix(Syllables(3, Db(-46)), RoomTone(3, Db(-85))));
        Assert.True(agc.Gain > 10, $"quiet speech in a quiet room should get a large boost, gain {agc.Gain}");

        var fan = RoomTone(4, Db(-58), seed: 2);
        var output = agc.Process(fan);

        var settled = output.AsSpan(2 * Rate);
        Assert.True(Rms(settled) <= AutomaticGainControlOptions.Default.NoiseCeilingRms * 1.25f,
            $"the louder room came out at {20 * Math.Log10(Rms(settled)):F1} dBFS");
    }

    // ── safety ──────────────────────────────────────────────────────────

    [Fact]
    public void Process_LoudBurstAfterQuietSpeech_ShouldNotClip()
    {
        var agc = new AutomaticGainControl();
        agc.Process(Mix(Syllables(3, Db(-46)), RoomTone(3, Db(-80))));
        var burst = agc.Process(Sine(0.5f, 0.5));

        // After the first 10 ms frame the gain has dropped below the clipping point.
        Assert.True(agc.Gain < 2);
        Assert.True(burst.Skip(160).All(s => Math.Abs(s) <= 0.9f + 1e-4f));
    }

    [Fact]
    public void Process_ShouldNotDependOnHowTheAudioIsBatched()
    {
        var input = Concat(RoomTone(2, Db(-60)), Mix(Syllables(4, Db(-40)), RoomTone(4, Db(-60), seed: 2)));
        var whole = new AutomaticGainControl().Process(input);

        var ragged = new AutomaticGainControl();
        var pieces = new List<float>();
        var sizes = new[] { 1, 7, 159, 160, 161, 3200, 4999 };
        for (int position = 0, k = 0; position < input.Length; k++)
        {
            var size = Math.Min(sizes[k % sizes.Length], input.Length - position);
            pieces.AddRange(ragged.Process(input.AsSpan(position, size)));
            position += size;
        }

        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void Options_ShouldSetTheTargetTheCapAndTheCeiling()
    {
        var input = Mix(Syllables(4, Db(-46)), RoomTone(4, Db(-62)));
        var noCeiling = new AutomaticGainControl(Rate, AutomaticGainControlOptions.Default with { NoiseCeilingRms = 0 });
        var withCeiling = new AutomaticGainControl();
        var capped = new AutomaticGainControl(Rate, AutomaticGainControlOptions.Default with { NoiseCeilingRms = 0, MaxGain = 4 });

        noCeiling.Process(input);
        withCeiling.Process(input);
        capped.Process(input);

        Assert.InRange(noCeiling.Gain, 10f, AutomaticGainControl.MaxGain);              // the speech alone asks for about +22 dB
        Assert.InRange(withCeiling.Gain, 1f, 4.5f);                                       // a -62 dBFS floor may be raised by 12 dB
        Assert.Equal(4f, capped.Gain);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Process_NonFiniteSamples_ShouldBecomeSilenceAndNeverPoisonTheGain(float bad)
    {
        // A faulty source (a broken resampler, an unplugged device driver) can deliver NaN or Infinity. Infinity used to make the
        // level estimate NaN for good, so the output stayed NaN after the source recovered.
        var agc = new AutomaticGainControl();
        agc.Process(Mix(Syllables(2, Db(-46)), RoomTone(2, Db(-80))));
        var glitch = Mix(Syllables(0.05, Db(-46)), RoomTone(0.05, Db(-80)));
        glitch[100] = bad;
        glitch[101] = bad;

        var during = agc.Process(glitch);
        var after = agc.Process(Mix(Syllables(2, Db(-46)), RoomTone(2, Db(-80), seed: 2)));

        Assert.All(during, s => Assert.True(float.IsFinite(s)));
        Assert.Equal(0f, during[100]);
        Assert.Equal(0f, during[101]);
        Assert.True(float.IsFinite(agc.Gain));
        Assert.All(after, s => Assert.True(float.IsFinite(s)));
        Assert.InRange(SyllableRms(after, 4), AutomaticGainControl.TargetRms * 0.8f, AutomaticGainControl.TargetRms * 1.2f);
    }

    [Fact]
    public void Process_ShouldNotModifyInput()
    {
        var agc = new AutomaticGainControl();
        var input = Mix(Syllables(1, Db(-46)), RoomTone(1, Db(-80)));
        var copy = input.ToArray();
        agc.Process(input);

        Assert.Equal(copy, input);
    }
}
