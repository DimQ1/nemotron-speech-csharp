using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The optional clean last mel frame (<see cref="NemotronRecognizerOptions.CleanLastFrame"/>): same chunk grid and same
/// words as the faithful GenAI front-end on speech clips, the token ids differ in a few percent of the clips.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronCleanFrontEndTests : IDisposable
{
    private readonly ITestOutputHelper output;

    // Each test loads a recognizer of its own (other options than the cached ones): in the collection it runs after the
    // other model classes, not beside them, and the cache gives its models back first.
    public E2E_NemotronCleanFrontEndTests(NemotronModelCache models, ITestOutputHelper output)
    {
        this.output = output;
        models.ReleaseAll();
    }

    public void Dispose()
    {
    }

    [NemotronModelFact]
    public void CleanLastFrame_ShouldKeepTheTimelineAndTheWordsOfSpeechClips()
    {
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        var golden = GoldenCase.LoadAll().Where(c => c.Variant == NemotronTestEnvironment.DefaultVariant && !c.Vad && c.HasAudio() && c.TokenCount > 0)
            .OrderBy(c => c.Name, StringComparer.Ordinal).Take(12).ToList();
        Assert.NotEmpty(golden);
        using var clean = new NemotronRnntRecognizer(directory, "cpu", null, false,
            new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads, CleanLastFrame = true });

        var identical = 0;
        foreach (var g in golden)
        {
            clean.ResetStreamingState();
            Assert.True(clean.TrySetLanguage(g.LangId ?? "0"));
            var audio = g.LoadAudio();

            var replay = Replay.Run(clean, audio, clean.ChunkSamples);

            // the same chunks on the same grid, whatever the tokens are
            var grid = (audio.Length + clean.ChunkSamples - 1) / clean.ChunkSamples;
            Assert.Equal(Enumerable.Range(0, grid), replay.Chunks.Select(c => c.Index));
            ParityAssert.TimelineInvariants(clean, replay.Tokens, audio.Length);

            var same = replay.Text.ToString() == g.Text;
            if (same)
                identical++;
            var distance = WordText.Distance(WordText.Words(g.Text), WordText.Words(replay.Text.ToString()));
            output.WriteLine($"{g.Name}: {(same ? "identical" : $"differs by {distance} word(s)")}");
            Assert.True(distance <= Math.Max(2, WordText.Words(g.Text).Length / 5), $"{g.Name}: clean front-end text moved by {distance} words");
        }
        output.WriteLine($"{identical} of {golden.Count} clips identical to the faithful (GenAI) text");
        Assert.True(identical >= golden.Count / 2);
    }

    [NemotronModelFact]
    public void CleanLastFrame_ChunkNeedsItsLookaheadBeforeItIsEncoded()
    {
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        using var clean = new NemotronRnntRecognizer(directory, "cpu", null, false,
            new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads, CleanLastFrame = true });
        var n = clean.ChunkSamples;

        Assert.Null(clean.ProcessAudio(new float[n]));                 // the chunk is complete but its 40 look-ahead samples are not
        Assert.Equal(0, clean.ChunksConsumed);
        Assert.NotNull(clean.ProcessAudio(new float[40]));
        Assert.Equal(1, clean.ChunksConsumed);
        Assert.Equal(40, clean.BufferedSamples);
        Assert.NotNull(clean.Flush());                                  // the 40 samples are padded to the next chunk
        Assert.Equal(2, clean.ChunksConsumed);
    }

    [NemotronModelFact]
    public void CleanLastFrame_WithChunkSizedFeeds_ShouldEncodeEveryChunkOneFeedLate()
    {
        // The documented cost of the option: not 2.5 ms of audio, a whole feed, because a chunk waits for the next feed's first samples.
        var directory = NemotronTestEnvironment.LocateModel(NemotronTestEnvironment.DefaultVariant)!;
        using var clean = new NemotronRnntRecognizer(directory, "cpu", null, false,
            new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads, CleanLastFrame = true });
        var n = clean.ChunkSamples;

        var encodedAfterFeed = new List<int>();
        for (var feed = 1; feed <= 4; feed++)
        {
            clean.ProcessAudio(new float[n]);
            encodedAfterFeed.Add(clean.ChunksConsumed);
        }

        Assert.Equal([0, 1, 2, 3], encodedAfterFeed);                  // faithful mode: [1, 2, 3, 4]
    }
}
