using SpeechLib.Nemotron;

namespace SpeechLib.Nemotron.Tests;

/// <summary>One call of the fake engine: what the recognizer handed it.</summary>
internal sealed record EngineCall(int ChunkIndex, float[] Chunk, float[] Lookahead, int Language);

/// <summary>
/// Chunk engine without a model: records every chunk it is given and emits the tokens a script asks for, so the
/// buffering, flush, silence-drop and timeline logic of the recognizer can be tested on its own.
/// </summary>
internal sealed class FakeChunkEngine(int chunkSamples = 8960, int framesPerChunk = 7, int lookahead = 0, bool supportsLanguage = true) : INemotronChunkEngine
{
    public List<EngineCall> Calls { get; } = new();
    public int ResetCount { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>(chunk index, chunk) to the (id, frame) pairs the chunk emits; default: no tokens.</summary>
    public Func<int, float[], IEnumerable<(int Id, int Frame)>> Script { get; set; } = (_, _) => [];

    public int ChunkSamples { get; } = chunkSamples;
    public int FramesPerChunk { get; } = framesPerChunk;
    public int LookaheadSamples { get; } = lookahead;
    public bool SupportsLanguage { get; } = supportsLanguage;
    public int LanguageId { get; set; }
    public float BlankPenalty { get; set; }
    public NemotronProviderInfo Provider { get; set; } = NemotronProviderInfo.Cpu(null);

    public void Reset() => ResetCount++;

    public int DecodeChunk(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookaheadSamples, int chunkIndex, List<NemotronToken> sink)
    {
        var copy = chunk.ToArray();
        Calls.Add(new EngineCall(chunkIndex, copy, lookaheadSamples.ToArray(), LanguageId));
        var count = 0;
        var symbolOfFrame = new Dictionary<int, int>();
        foreach (var (id, frame) in Script(chunkIndex, copy))
        {
            symbolOfFrame.TryGetValue(frame, out var symbol);
            symbolOfFrame[frame] = symbol + 1;
            sink.Add(new NemotronToken(id, chunkIndex, frame, symbol, (long)chunkIndex * FramesPerChunk + frame, -0.1f));
            count++;
        }
        return count;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>One call of the branching fake engine.</summary>
/// <param name="Branch">The branch that decoded the chunk.</param>
/// <param name="Lineage">Which stream state decoded it: 0 = the state of the stream since its start, then one per reset of a branch.</param>
/// <param name="ChunkIndex">Index on the stream grid.</param>
/// <param name="FirstSample">The chunk's first sample (the test audio is a ramp: its stream position).</param>
internal sealed record BranchCall(int Branch, int Lineage, int ChunkIndex, float FirstSample);

/// <summary>
/// Chunk engine with two branches and no model: each branch has its own "stream state" (a lineage number that a reset renews and
/// a count of chunks it decoded since), and a script decides the tokens from (lineage, chunk index, chunks since the reset), so
/// the pause-state policy can be tested on its own.
/// </summary>
internal sealed class FakeBranchingEngine(int chunkSamples = 8960, int framesPerChunk = 7) : INemotronChunkEngine
{
    private readonly int[] _lineage = new int[2];
    private readonly int[] _decoded = new int[2];
    private int _lineages;

    public List<BranchCall> Calls { get; } = new();
    public List<(int Branch, int Lineage)> Resets { get; } = new();
    public int Selections { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>(lineage, chunk index, chunks this lineage decoded before) to the (id, frame) pairs of the chunk; default: none.</summary>
    public Func<int, int, int, IEnumerable<(int Id, int Frame)>> Script { get; set; } = (_, _, _) => [];

    public int ChunkSamples { get; } = chunkSamples;
    public int FramesPerChunk { get; } = framesPerChunk;
    public int LookaheadSamples => 0;
    public bool SupportsLanguage => true;
    public int LanguageId { get; set; }
    public float BlankPenalty { get; set; }
    public int BranchCount => 2;
    public int ActiveBranch { get; private set; }

    /// <summary>The lineage of the active branch.</summary>
    public int ActiveLineage => _lineage[ActiveBranch];

    public void Reset()
    {
        _lineage[ActiveBranch] = ++_lineages;
        _decoded[ActiveBranch] = 0;
        Resets.Add((ActiveBranch, _lineage[ActiveBranch]));
    }

    public void SelectBranch(int branch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(branch);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(branch, 1);
        if (branch == ActiveBranch)
            return;
        Selections++;
        ActiveBranch = branch;
    }

    public int DecodeChunk(ReadOnlySpan<float> chunk, ReadOnlySpan<float> lookaheadSamples, int chunkIndex, List<NemotronToken> sink)
    {
        var branch = ActiveBranch;
        Calls.Add(new BranchCall(branch, _lineage[branch], chunkIndex, chunk[0]));
        var count = 0;
        var symbolOfFrame = new Dictionary<int, int>();
        foreach (var (id, frame) in Script(_lineage[branch], chunkIndex, _decoded[branch]))
        {
            symbolOfFrame.TryGetValue(frame, out var symbol);
            symbolOfFrame[frame] = symbol + 1;
            sink.Add(new NemotronToken(id, chunkIndex, frame, symbol, (long)chunkIndex * FramesPerChunk + frame, -0.1f));
            count++;
        }
        _decoded[branch]++;
        return count;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Speech gate that reads the speech decision from the chunk itself: the test audio has sample value = stream sample
/// index, so the chunk index is known from its first sample.
/// </summary>
internal sealed class FakeGate(int chunkSamples, Func<int, bool> hasSpeech) : IChunkSpeechGate
{
    public List<int> Scored { get; } = new();
    public int ResetCount { get; private set; }
    public bool Disposed { get; private set; }

    public bool ContainsSpeech(ReadOnlySpan<float> chunk)
    {
        var index = (int)(chunk[0] / chunkSamples);
        Scored.Add(index);
        return hasSpeech(index);
    }

    public void Reset() => ResetCount++;

    public void Dispose() => Disposed = true;
}

internal static class TestAudio
{
    /// <summary>A ramp whose sample value equals its stream index (exact in float up to 2^24), starting at <paramref name="start"/>.</summary>
    public static float[] Ramp(int length, int start = 0)
    {
        var audio = new float[length];
        for (var i = 0; i < length; i++)
            audio[i] = start + i;
        return audio;
    }

    /// <summary>
    /// Quiet speech-like input for the gain control: 180 ms syllables (two harmonics, 15 ms ramps) of RMS <paramref name="syllableRms"/>
    /// every 300 ms, three to a word, over a Gaussian room tone of RMS <paramref name="toneRms"/> (fixed seed). A steady sine is not
    /// speech for the gain control, and digital silence between the syllables would be no room at all.
    /// </summary>
    public static float[] Syllables(int length, float syllableRms, float toneRms = 0.0001f, int seed = 1)
    {
        const int period = 4800, syllable = 2880;
        var random = new Random(seed);
        var audio = new float[length];
        for (var i = 0; i < length; i++)
        {
            var slot = i % period;
            var voiced = i / period % 4 != 3 && slot < syllable;
            var ramp = Math.Min(1.0, Math.Min(slot, syllable - slot) / 240.0);
            var t = i / 16000.0;
            var speech = voiced ? syllableRms * Math.Sqrt(2) * ramp * (0.8 * Math.Sin(2 * Math.PI * 180 * t) + 0.6 * Math.Sin(2 * Math.PI * 360 * t)) : 0;
            var tone = toneRms * Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
            audio[i] = (float)(speech + tone);
        }
        return audio;
    }

    /// <summary>Splits <paramref name="audio"/> into pieces of the given sizes (the last piece takes the rest, sizes repeat).</summary>
    public static IEnumerable<float[]> Split(float[] audio, params int[] sizes)
    {
        var position = 0;
        var i = 0;
        while (position < audio.Length)
        {
            var size = Math.Min(sizes[i++ % sizes.Length], audio.Length - position);
            yield return audio[position..(position + size)];
            position += size;
        }
    }
}
