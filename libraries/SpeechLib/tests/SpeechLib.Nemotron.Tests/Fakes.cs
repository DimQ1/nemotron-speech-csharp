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
