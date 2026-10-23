using SpeechLib.Audio;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_SilenceDropRuleTests
{
    private sealed class ScriptedGate(params bool[] speech) : IChunkSpeechGate
    {
        private int _next;
        public int Scored => _next;
        public int Resets { get; private set; }
        public bool ContainsSpeech(ReadOnlySpan<float> chunk) => speech[_next++];
        public void Reset() => Resets++;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("int4-c056", 8960, 6, 1, 6)]     // 3360 ms / 560 ms = 6 silent chunks, 560 ms prefix = 1
    [InlineData("int4-c112", 17920, 3, 1, 3)]    // 3360 ms / 1120 ms = 3
    public void ChunkCounts_ShippedConfigs_ShouldDivideExactly(string variant, int chunkSamples, int silence, int prefix, int threshold)
    {
        var config = NemotronTestEnvironment.LoadConfig(variant);

        var (silenceChunks, prefixChunks) = SilenceDropRule.ChunkCounts(config.Vad!, chunkSamples, config.SampleRate);
        var rule = new SilenceDropRule(new ScriptedGate(), silenceChunks, prefixChunks);

        Assert.Equal(silence, silenceChunks);
        Assert.Equal(prefix, prefixChunks);
        Assert.Equal(threshold, rule.DropThreshold);
    }

    [Theory]
    [InlineData(500, 300, 8960, 1, 1)]           // upstream defaults on a 560 ms chunk: max(1, 0)
    [InlineData(1000, 100, 8960, 1, 1)]          // 1.79 chunks truncate to 1
    [InlineData(5600, 1700, 8960, 10, 3)]
    [InlineData(0, 0, 8960, 1, 1)]
    public void ChunkCounts_ShouldTruncateAndHaveAFloorOfOne(int silenceMs, int prefixMs, int chunkSamples, int silence, int prefix)
    {
        var vad = new NemotronVadConfig { FileName = "x", SilenceDurationMs = silenceMs, PrefixPaddingMs = prefixMs };

        Assert.Equal((silence, prefix), SilenceDropRule.ChunkCounts(vad, chunkSamples, 16000));
    }

    [Fact]
    public void ShouldDrop_ShouldStartAtTheThresholdAndStayUntilSpeech()
    {
        // 2 speech, 8 silent, 1 speech, 3 silent
        var gate = new ScriptedGate(true, true, false, false, false, false, false, false, false, false, true, false, false, false);
        var rule = new SilenceDropRule(gate, silenceDurationChunks: 6, prefixPaddingChunks: 1);

        var decisions = Enumerable.Range(0, 14).Select(_ => rule.ShouldDrop(new float[10])).ToArray();

        Assert.Equal(
            new[] {false, false,                       // speech
             false, false, false, false, false,  // silent chunks 1-5: kept as context
             true, true, true,                   // silent chunks 6, 7, 8: dropped
             false,                              // speech again
             false, false, false },              // a new silent run starts counting from 1
            decisions);
        Assert.Equal(14, gate.Scored);
        Assert.Equal(3, rule.ConsecutiveSilentChunks);
    }

    [Fact]
    public void DropThreshold_ShouldBeTheLargerOfPrefixAndSilence()
    {
        Assert.Equal(6, new SilenceDropRule(new ScriptedGate(), 6, 1).DropThreshold);
        Assert.Equal(9, new SilenceDropRule(new ScriptedGate(), 6, 9).DropThreshold);
        Assert.Equal(1, new SilenceDropRule(new ScriptedGate(), 0, 0).DropThreshold);   // floors of one
    }

    [Fact]
    public void Reset_ShouldClearTheCountAndTheGate()
    {
        var gate = new ScriptedGate(false, false, false);
        var rule = new SilenceDropRule(gate, 6, 1);
        rule.ShouldDrop(new float[1]);
        rule.ShouldDrop(new float[1]);

        rule.Reset();

        Assert.Equal(0, rule.ConsecutiveSilentChunks);
        Assert.Equal(1, gate.Resets);
    }

    private sealed class FakeVadFilter(bool speech) : IVadFilter
    {
        public int Resets { get; private set; }
        public bool Disposed { get; private set; }
        public int LastLength { get; private set; }
        public float LastProbability => speech ? 1f : 0f;
        public bool HasSpeech(ReadOnlySpan<float> samples)
        {
            LastLength = samples.Length;
            return speech;
        }
        public void Reset() => Resets++;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void VadFilterGate_ShouldDelegateAndOnlyDisposeAFilterItOwns()
    {
        var shared = new FakeVadFilter(speech: true);
        var gate = new VadFilterGate(shared, ownsFilter: false);

        Assert.True(gate.ContainsSpeech(new float[8960]));
        Assert.Equal(8960, shared.LastLength);
        gate.Reset();
        gate.Dispose();
        Assert.Equal(1, shared.Resets);
        Assert.False(shared.Disposed);

        var owned = new FakeVadFilter(speech: false);
        var ownedGate = new VadFilterGate(owned, ownsFilter: true);
        Assert.False(ownedGate.ContainsSpeech(new float[10]));
        ownedGate.Dispose();
        Assert.True(owned.Disposed);
    }
}
