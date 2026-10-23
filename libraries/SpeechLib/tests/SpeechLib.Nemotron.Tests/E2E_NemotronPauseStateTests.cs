using System.Text;
using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The pause-state policy and the flush closing chunks on the real model: switching branches is exact, a model-state reset gives a
/// new stream on the same clock, the dual-branch policy recovers the words a kept state drops after a pause and keeps a sentence
/// that continues after a hesitation, its results do not depend on batching, and a closing chunk gives back the late last word.
/// Skipped without the models or the CV17 test audio.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronPauseStateTests(NemotronModelCache models, ITestOutputHelper output)
{
    private const int Rate = 16000;

    private static NemotronPauseStateOptions Dual => new() { Policy = NemotronPauseState.DualBranch };

    /// <summary>Room tone like the golden generator: white noise of 66 LSB rms (-54 dBFS), never digital zeros.</summary>
    private static float[] RoomTone(double seconds, int seed)
    {
        var random = new Random(seed);
        var tone = new float[(int)(seconds * Rate)];
        for (var i = 0; i < tone.Length; i++)
        {
            var u1 = 1.0 - random.NextDouble();
            var u2 = random.NextDouble();
            tone[i] = (float)(66.0 / 32768 * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return tone;
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private NemotronRnntRecognizer Load(string variant, NemotronRecognizerOptions options)
    {
        models.ReleaseAll();   // never two copies of a model in memory
        var directory = NemotronTestEnvironment.LocateModel(variant)!;
        return new NemotronRnntRecognizer(directory, "cpu", "en", useVad: true, options with { CpuThreads = NemotronTestEnvironment.Threads });
    }

    private static (string Text, List<NemotronToken> Tokens) Stream(NemotronRnntRecognizer recognizer, float[] audio, int batch)
    {
        recognizer.ResetStreamingState();
        var text = new StringBuilder();
        var tokens = new List<NemotronToken>();
        foreach (var piece in TestAudio.Split(audio, batch))
        {
            text.Append(recognizer.ProcessAudio(piece));
            tokens.AddRange(recognizer.LastTokens);
        }
        text.Append(recognizer.Flush());
        tokens.AddRange(recognizer.LastTokens);
        return (text.ToString(), tokens);
    }

    // ── the engine's branches ───────────────────────────────────────────

    [NemotronModelTheory]
    [InlineData("int4-c056")]
    public void SelectBranch_ShouldLeaveEachBranchExactlyWhereItWas(string variant)
    {
        var recognizer = models.Get(variant);
        var engine = recognizer.Engine;
        var a = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");
        var b = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0006.wav");
        var n = engine.ChunkSamples;
        var chunks = Math.Max(a.Length, b.Length) / n;

        List<NemotronToken> Alone(float[] audio)
        {
            engine.Reset();
            var sink = new List<NemotronToken>();
            for (var k = 0; k < audio.Length / n; k++)
                engine.DecodeChunk(audio.AsSpan(k * n, n), default, k, sink);
            return sink;
        }

        var expectedA = Alone(a);
        var expectedB = Alone(b);
        var tokensA = new List<NemotronToken>();
        var tokensB = new List<NemotronToken>();
        try
        {
            engine.SelectBranch(1);
            engine.Reset();
            engine.SelectBranch(0);
            engine.Reset();
            for (var k = 0; k < chunks; k++)
            {
                // alternate the order too: the branch that is active first changes every chunk
                var (first, second) = k % 2 == 0 ? (0, 1) : (1, 0);
                foreach (var branch in new[] { first, second })
                {
                    var audio = branch == 0 ? a : b;
                    if ((k + 1) * n > audio.Length)
                        continue;
                    engine.SelectBranch(branch);
                    engine.DecodeChunk(audio.AsSpan(k * n, n), default, k, branch == 0 ? tokensA : tokensB);
                }
            }
        }
        finally
        {
            engine.SelectBranch(0);
            recognizer.ResetStreamingState();
        }

        Assert.NotEmpty(expectedA);
        Assert.NotEmpty(expectedB);
        Assert.Equal(expectedA, tokensA);   // ids, positions and log-probabilities, bit for bit
        Assert.Equal(expectedB, tokensB);
    }

    [NemotronModelTheory]
    [InlineData("int4-c056")]
    public void ResetModelState_ShouldGiveTheTokensOfANewStreamOnTheSameClock(string variant)
    {
        var recognizer = models.Get(variant);
        var n = recognizer.ChunkSamples;
        var a = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");
        var b = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0002.wav");
        var aChunks = a.Length / n;

        var alone = Stream(recognizer, b, n).Tokens;
        Assert.NotEmpty(alone);

        recognizer.ResetStreamingState();
        recognizer.ProcessAudio(a.AsSpan(0, aChunks * n));
        Assert.Null(recognizer.ResetModelState());
        var after = Stream2(recognizer, b, n);

        var framesPerChunk = recognizer.FramesPerChunk;
        Assert.Equal(alone.Select(t => t with { Chunk = t.Chunk + aChunks, GlobalFrame = t.GlobalFrame + (long)aChunks * framesPerChunk }), after);
        recognizer.ResetStreamingState();

        static List<NemotronToken> Stream2(NemotronRnntRecognizer recognizer, float[] audio, int batch)
        {
            var tokens = new List<NemotronToken>();
            foreach (var piece in TestAudio.Split(audio, batch))
            {
                recognizer.ProcessAudio(piece);
                tokens.AddRange(recognizer.LastTokens);
            }
            recognizer.Flush();
            tokens.AddRange(recognizer.LastTokens);
            return tokens;
        }
    }

    // ── the policy on the real model ────────────────────────────────────

    private static string[] Words(string text) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"<[^>\s]+>", " "), @"[^\p{L}\p{N}]+", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private (string Keep, string Dual, List<NemotronPauseDecision> Decisions) KeepAndDual(string variant, float[] audio)
    {
        string keep;
        using (var recognizer = Load(variant, new NemotronRecognizerOptions()))
            keep = Stream(recognizer, audio, recognizer.ChunkSamples).Text;
        using var dual = Load(variant, new NemotronRecognizerOptions { PauseState = Dual });
        var decisions = new List<NemotronPauseDecision>();
        dual.PauseDecided += decisions.Add;
        var text = Stream(dual, audio, dual.ChunkSamples).Text;
        output.WriteLine($"keep: {keep}");
        output.WriteLine($"dual: {text}");
        foreach (var d in decisions)
            output.WriteLine($"  chunk {d.FirstChunk}: keep {d.KeepLetters} '{d.KeepText.Trim()}' / fresh {d.FreshLetters} '{d.FreshText.Trim()}' -> {(d.FreshWon ? "fresh" : "keep")}");
        return (keep, text, decisions);
    }

    // CV17 en 0124 + 2 s of room tone + 0125 (pairs-g2/en/0013 of the A1 measurement): the kept state writes the closing mark of the first
    // sentence and then nothing for the whole second one ("Thai dancers happily going through their steps in unison"); a fresh state hears it.
    [NemotronModelTheory]
    [InlineData("int4-c056")]
    public void DualBranch_NewSentenceAfterAPause_ShouldDecodeTheSentenceTheKeptStateDrops(string variant)
    {
        var a = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0124.wav");
        var b = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0125.wav");
        var audio = Concat(RoomTone(0.5, 11), a, RoomTone(2, 12), b, RoomTone(1, 13));
        var second = Words(File.ReadAllText(NemotronTestEnvironment.FindAudio("cv17/en/0125.txt")!));

        var (keep, dual, decisions) = KeepAndDual(variant, audio);

        int Heard(string text) => second.Count(w => Words(text).Contains(w));
        Assert.Contains(decisions, d => d.FreshWon);
        Assert.True(Heard(dual) >= Heard(keep) + 4, $"the second sentence: {Heard(keep)} of {second.Length} words kept, {Heard(dual)} dual");
        Assert.Contains("<en-US>", dual);   // the closing tag of the first sentence comes from the kept state
    }

    // CV17 en 0142 split at its quietest point by 2 s of room tone (hes-g2/en/0039): the speech after the hesitation continues the sentence,
    // the kept state decodes it ("... come to test its beers"), a fresh state loses it; the policy must keep the state.
    [NemotronModelTheory]
    [InlineData("int4-c056")]
    public void DualBranch_ASentenceThatContinuesAfterAHesitation_ShouldKeepTheState(string variant)
    {
        var clip = NemotronTestEnvironment.LoadAudioRelative("cv17/en/0142.wav");
        const int split = 51795;
        var audio = Concat(RoomTone(0.5, 21), clip[..split], RoomTone(2, 22), clip[split..], RoomTone(1, 23));

        var (keep, dual, decisions) = KeepAndDual(variant, audio);

        Assert.NotEmpty(decisions);
        Assert.DoesNotContain(decisions, d => d.FreshWon);
        Assert.Equal(keep, dual);
    }

    // A2: the stream ends 100 ms after the last speech window (the user presses Stop right after the word, a file cut tightly). GenAI's single
    // padded chunk leaves the last tokens of the last word unwritten ("You are so"); one closing chunk of zeros gives them back.
    [NemotronModelTheory]
    [InlineData("int4-c056", "cv17/en/0010.wav", 32832, "rude")]
    [InlineData("int4-c056", "cv17/en/0018.wav", 62016, "heath")]
    [InlineData("int4-c056", "cv17/ru/0005.wav", 51264, "палестины")]
    public void FlushClosingChunks_ShouldGiveBackTheLateLastWordOfAStreamThatEndsAfterTheSpeech(string variant, string clip, int cut, string lastWord)
    {
        var audio = NemotronTestEnvironment.LoadAudioRelative(clip)[..cut];
        var language = clip.Contains("/ru/", StringComparison.Ordinal) ? "ru" : "en";

        string Transcribe(int closing)
        {
            models.ReleaseAll();
            var directory = NemotronTestEnvironment.LocateModel(variant)!;
            using var recognizer = new NemotronRnntRecognizer(directory, "cpu", language, useVad: true,
                new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads, FlushClosingChunks = closing });
            return Stream(recognizer, audio, recognizer.ChunkSamples).Text;
        }

        var single = Transcribe(0);
        var closed = Transcribe(1);
        output.WriteLine($"closing 0: {single}");
        output.WriteLine($"closing 1: {closed}");

        Assert.NotEqual(lastWord, Words(single)[^1]);
        Assert.Equal(lastWord, Words(closed)[^1]);
        Assert.StartsWith(single, closed);   // the chunks before are the same: the closing chunk only adds the late tokens
    }

    [NemotronModelTheory]
    [InlineData("int4-c056")]
    public void DualBranch_ShouldNotDependOnHowTheAudioIsBatched(string variant)
    {
        var audio = Concat(RoomTone(0.5, 1), NemotronTestEnvironment.LoadAudioRelative("cv17/en/0106.wav"), RoomTone(2, 2),
            NemotronTestEnvironment.LoadAudioRelative("cv17/en/0107.wav"), RoomTone(1, 3));
        using var recognizer = Load(variant, new NemotronRecognizerOptions { PauseState = Dual });
        var n = recognizer.ChunkSamples;

        var expected = Stream(recognizer, audio, n);
        Assert.True(recognizer.PauseForks >= 1, "the pause should start the policy");
        foreach (var batch in new[] { 3200, 1, 3 * n - 7, audio.Length })
        {
            var actual = Stream(recognizer, audio, batch);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Tokens, actual.Tokens);
        }
        output.WriteLine($"{recognizer.PauseForks} fork(s), {recognizer.FreshBranchWins} fresh: {expected.Text}");
    }
}
