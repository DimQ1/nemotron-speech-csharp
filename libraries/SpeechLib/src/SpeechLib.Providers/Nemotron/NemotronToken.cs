namespace SpeechLib.Nemotron;

/// <summary>
/// One non-blank token of the RNN-T search with its place on the absolute timeline of the stream.
/// </summary>
/// <remarks>
/// <para>The timeline counts every sample fed since the last reset, in chunks of
/// <see cref="NemotronRnntRecognizer.ChunkSamples"/>: chunk <c>k</c> covers samples
/// <c>[k * ChunkSamples, (k + 1) * ChunkSamples)</c> of the stream, <b>including chunks that the silence gate dropped
/// without running the model</b> and independent of how the caller batched the audio. Each chunk yields
/// <see cref="NemotronRnntRecognizer.FramesPerChunk"/> encoder frames of
/// <see cref="NemotronRnntRecognizer.SamplesPerFrame"/> samples (80 ms), so the global frame
/// <see cref="GlobalFrame"/> <c>g = Chunk * FramesPerChunk + Frame</c> owns the sample slot
/// <c>[g * SamplesPerFrame, (g + 1) * SamplesPerFrame)</c>. The frame is when the model decided to emit the token,
/// not where the sound is: it lags the acoustic start of a word (measured median 176 to 331 ms).</para>
/// <para>The padded tail chunk produced by <c>Flush</c> keeps its place on the grid, so a stream that continues
/// after a flush resumes at the next chunk boundary.</para>
/// </remarks>
/// <param name="Id">Vocabulary id (never the blank).</param>
/// <param name="Chunk">Index of the chunk on the stream grid, counting dropped chunks.</param>
/// <param name="Frame">Encoder frame inside the chunk, 0 to FramesPerChunk - 1.</param>
/// <param name="Symbol">Position among the tokens emitted on the same frame (0 for the first).</param>
/// <param name="GlobalFrame"><c>Chunk * FramesPerChunk + Frame</c>.</param>
/// <param name="LogProb">Joint log-probability of the token.</param>
public readonly record struct NemotronToken(int Id, int Chunk, int Frame, int Symbol, long GlobalFrame, float LogProb)
{
    /// <summary>First sample of the token's frame slot on the stream timeline (<c>GlobalFrame * samplesPerFrame</c>).</summary>
    public long SlotStartSample(int samplesPerFrame) => GlobalFrame * samplesPerFrame;

    /// <summary>One past the last sample of the token's frame slot.</summary>
    public long SlotEndSample(int samplesPerFrame) => (GlobalFrame + 1) * samplesPerFrame;
}

/// <summary>What happened to one chunk of the stream during the most recent call.</summary>
/// <param name="Index">Chunk index on the stream grid.</param>
/// <param name="Dropped">The silence gate dropped it: no state was touched and it carries no tokens.</param>
/// <param name="FirstToken">Index of its first token in <see cref="NemotronRnntRecognizer.LastTokens"/>.</param>
/// <param name="TokenCount">Number of tokens it emitted.</param>
public readonly record struct NemotronChunkInfo(int Index, bool Dropped, int FirstToken, int TokenCount);
