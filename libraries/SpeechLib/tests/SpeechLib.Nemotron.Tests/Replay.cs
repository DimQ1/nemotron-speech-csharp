using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>Public entry points for <c>[MemberData]</c> over the internal golden loader.</summary>
public static class GoldenData
{
    /// <summary>Every golden recorded without VAD: (variant, name). One whose model or audio is missing is a skipped row, not a missing one.</summary>
    public static IEnumerable<object[]> Parity() => GoldenCase.ParityRows();

    /// <summary>Goldens recorded with GenAI's native VAD on (rows without their model or audio are skipped rows).</summary>
    public static IEnumerable<object[]> Vad() => GoldenCase.VadRows();

    /// <summary>A few runnable cases per variant for the tests that replay each clip several times.</summary>
    public static IEnumerable<object[]> Subset() =>
        GoldenCase.Enumerate(vad: false)
            .GroupBy(c => c.Variant)
            .SelectMany(g => g.OrderBy(c => c.Name, StringComparer.Ordinal).Take(3))
            .Select(c => new object[] { c.Variant, c.Name });
}

/// <summary>What a chunk of a replay produced.</summary>
internal sealed record ReplayChunk(int Index, bool Dropped, NemotronToken[] Tokens);

/// <summary>A stream pushed through a recognizer: its tokens, chunks and text, collected call by call.</summary>
internal sealed class Replay
{
    public List<NemotronToken> Tokens { get; } = new();
    public List<ReplayChunk> Chunks { get; } = new();
    public System.Text.StringBuilder Text { get; } = new();
    public int Calls { get; private set; }
    public int NullCalls { get; private set; }

    /// <summary>Feeds the pieces (calling <c>ProcessAudio</c> for each) and then <c>Flush</c>.</summary>
    public static Replay Run(NemotronRnntRecognizer recognizer, IEnumerable<float[]> pieces, bool flush = true)
    {
        var replay = new Replay();
        foreach (var piece in pieces)
            replay.Collect(recognizer, recognizer.ProcessAudio(piece));
        if (flush)
            replay.Collect(recognizer, recognizer.Flush());
        return replay;
    }

    /// <summary>Feeds the audio in pieces of <paramref name="piece"/> samples.</summary>
    public static Replay Run(NemotronRnntRecognizer recognizer, float[] audio, int piece, bool flush = true) =>
        Run(recognizer, TestAudio.Split(audio, piece), flush);

    /// <summary>Records what the recognizer's most recent call returned and produced.</summary>
    public void Collect(NemotronRnntRecognizer recognizer, string? text)
    {
        Calls++;
        if (text is null)
            NullCalls++;
        else
            Text.Append(text);

        var tokens = recognizer.LastTokens.ToArray();
        Tokens.AddRange(tokens);
        foreach (var chunk in recognizer.LastChunks)
            Chunks.Add(new ReplayChunk(chunk.Index, chunk.Dropped, tokens[chunk.FirstToken..(chunk.FirstToken + chunk.TokenCount)]));
    }

    /// <summary>Flushes the recognizer and records the result.</summary>
    public void CollectFlush(NemotronRnntRecognizer recognizer) => Collect(recognizer, recognizer.Flush());

    public IEnumerable<int> DroppedChunks => Chunks.Where(c => c.Dropped).Select(c => c.Index);

    /// <summary>The token ids of every encoded chunk, by chunk index.</summary>
    public Dictionary<int, int[]> IdsByChunk() =>
        Chunks.Where(c => !c.Dropped).ToDictionary(c => c.Index, c => c.Tokens.Select(t => t.Id).ToArray());

    /// <summary>The tokens as comparable tuples (everything the timeline records except the log-probability).</summary>
    public (int Id, int Chunk, int Frame, int Symbol, long Global)[] Positions() =>
        Tokens.Select(t => (t.Id, t.Chunk, t.Frame, t.Symbol, t.GlobalFrame)).ToArray();
}

/// <summary>Word level comparison of two transcripts, for the tests that check "sensible text" instead of token parity.</summary>
internal static class WordText
{
    public static string[] Words(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The text without its language tags (<c>&lt;en-US&gt;</c>, <c>&lt;ru-RU&gt;</c>), which the model writes after a sentence.</summary>
    public static string WithoutLanguageTags(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"<\w{2,3}(-\w{2,4})?>", " ");

    /// <summary>Word level edit distance (insertions, deletions, substitutions).</summary>
    public static int Distance(string[] a, string[] b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}

internal static class ParityAssert
{
    /// <summary>
    /// Compares a replay with a GenAI golden chunk by chunk: the same chunks encoded, the same token ids in each, the
    /// same dropped chunks, the same text. Returns (chunks compared, tokens compared).
    /// </summary>
    public static (int Chunks, int Tokens) MatchesGolden(GoldenCase golden, int audioSamples, Replay replay)
    {
        var chunkSamples = golden.ChunkSamples;
        var fullChunks = audioSamples / chunkSamples;

        var expectedIds = new Dictionary<int, int[]>();
        var expectedDropped = new HashSet<int>();
        foreach (var entry in golden.Chunks)
        {
            if (entry.Index == -1)
                expectedIds[fullChunks] = entry.Ids;                  // Flush() encodes the remainder as the next chunk of the grid
            else if (entry.Processed)
                expectedIds[entry.Index] = entry.Ids;
            else if (entry.Index < fullChunks)
                expectedDropped.Add(entry.Index);                     // GenAI returned nothing for a complete chunk: its VAD dropped it
            // else: the partial last piece, which stays in the buffer until the flush
        }

        var actualIds = replay.IdsByChunk();
        Assert.Equal(expectedIds.Keys.OrderBy(k => k), actualIds.Keys.OrderBy(k => k));
        Assert.Equal(expectedDropped.OrderBy(k => k), replay.DroppedChunks.OrderBy(k => k));
        foreach (var (index, ids) in expectedIds)
            Assert.True(ids.SequenceEqual(actualIds[index]),
                $"{golden.Name} chunk {index}: GenAI [{string.Join(",", ids)}] but got [{string.Join(",", actualIds[index])}]");

        Assert.Equal(golden.Text, replay.Text.ToString());
        var tokens = expectedIds.Values.Sum(ids => ids.Length);
        Assert.Equal(tokens, replay.Tokens.Count);
        if (golden.TokenCount > 0)
            Assert.Equal(golden.TokenCount, replay.Tokens.Count);
        return (expectedIds.Count + expectedDropped.Count, tokens);
    }

    /// <summary>The timeline contract of <see cref="NemotronToken"/>.</summary>
    public static void TimelineInvariants(NemotronRnntRecognizer recognizer, IReadOnlyList<NemotronToken> tokens, int audioSamples)
    {
        var chunkSamples = recognizer.ChunkSamples;
        var frames = recognizer.FramesPerChunk;
        var lastChunk = (audioSamples + chunkSamples - 1) / chunkSamples - 1;   // the flush chunk is the last one on the grid

        NemotronToken? previous = null;
        foreach (var token in tokens)
        {
            Assert.InRange(token.Frame, 0, frames - 1);
            Assert.InRange(token.Symbol, 0, recognizer.Config.MaxSymbolsPerStep - 1);
            Assert.Equal((long)token.Chunk * frames + token.Frame, token.GlobalFrame);
            Assert.InRange(token.Chunk, 0, lastChunk);
            Assert.True(token.LogProb <= 0f && !float.IsNaN(token.LogProb), "log-probabilities are <= 0");

            // tokens strictly ordered by (chunk, frame, symbol)
            if (previous is { } p)
                Assert.True((token.Chunk, token.Frame, token.Symbol).CompareTo((p.Chunk, p.Frame, p.Symbol)) > 0,
                    $"token order broke at chunk {token.Chunk} frame {token.Frame} symbol {token.Symbol}");
            previous = token;

            // the 80 ms slot of the frame starts inside the audio, except in the zero-padded flush chunk, where it
            // may lie up to a chunk beyond the last real sample
            var slotStart = token.GlobalFrame * recognizer.SamplesPerFrame;
            Assert.True(slotStart < (long)(lastChunk + 1) * chunkSamples);
            if (token.Chunk < lastChunk || audioSamples % chunkSamples == 0)
                Assert.True(slotStart < audioSamples, $"token in chunk {token.Chunk} starts at sample {slotStart} of {audioSamples}");
        }
    }
}
