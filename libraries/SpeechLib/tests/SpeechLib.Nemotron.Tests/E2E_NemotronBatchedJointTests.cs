using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The batched joint (<see cref="NemotronRecognizerOptions.BatchedJoint"/>) on the real models of every variant: each row of a
/// batch is bit-identical to a one-frame run, for every start frame and batch size, on real encoder frames and real prediction
/// outputs; and whole streams give the same tokens with the same scores both ways. The golden replays are the oracle against
/// GenAI (which ran the joint frame by frame); these tests are why a batched run cannot drift from it on this platform.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronBatchedJointTests(NemotronModelCache models, ITestOutputHelper output)
{
    private static readonly string[] RowClips = ["cv17/en/0001.wav", "cv17/ru/0001.wav"];
    private static readonly string[] StreamClips = ["cv17/en/0001.wav", "cv17/en/0002.wav", "cv17/en/0003.wav", "cv17/ru/0001.wav", "cv17/ru/0002.wav"];

    [NemotronModelFact("int4-c056")]
    public void JoinFrames_Int4C056_ShouldGiveTheRowsOfOneFrameRuns() => AssertRowsMatchOneFrameRuns("int4-c056");

    [NemotronModelFact("int4-c112")]
    public void JoinFrames_Int4C112_ShouldGiveTheRowsOfOneFrameRuns() => AssertRowsMatchOneFrameRuns("int4-c112");

    [NemotronModelFact("fp32-c056")]
    public void JoinFrames_Fp32C056_ShouldGiveTheRowsOfOneFrameRuns() => AssertRowsMatchOneFrameRuns("fp32-c056");

    [NemotronModelFact("fp32-c112")]
    public void JoinFrames_Fp32C112_ShouldGiveTheRowsOfOneFrameRuns() => AssertRowsMatchOneFrameRuns("fp32-c112");

    [NemotronModelFact("int4-c056")]
    public void BatchedJoint_Int4C056_ShouldDecodeLikeOneFramePerRun() => AssertStreamsMatch("int4-c056");

    [NemotronModelFact("int4-c112")]
    public void BatchedJoint_Int4C112_ShouldDecodeLikeOneFramePerRun() => AssertStreamsMatch("int4-c112");

    [NemotronModelFact("fp32-c056")]
    public void BatchedJoint_Fp32C056_ShouldDecodeLikeOneFramePerRun() => AssertStreamsMatch("fp32-c056");

    [NemotronModelFact("fp32-c112")]
    public void BatchedJoint_Fp32C112_ShouldDecodeLikeOneFramePerRun() => AssertStreamsMatch("fp32-c112");

    // After every chunk of a clip, with the chunk's encoder frames in place: the decoder's own prediction output and two
    // others, every start frame, every batch size, compared bit for bit with one-frame runs of the same frames.
    private void AssertRowsMatchOneFrameRuns(string variant)
    {
        var recognizer = models.Get(variant);
        var engine = (NemotronRnntEngine)recognizer.Engine;
        var step = engine.StepModel;
        var frames = recognizer.FramesPerChunk;
        var vocab = recognizer.Config.VocabSize;
        var single = new int[frames * vocab];
        var rng = new Random(5);
        long rows = 0, runs = 0;

        foreach (var clip in RowClips)
        {
            recognizer.ResetStreamingState();
            Assert.True(recognizer.TrySetLanguage(clip.Contains("/ru/") ? "ru" : "en"));
            var audio = NemotronTestEnvironment.LoadAudioRelative(clip);
            for (var start = 0; start + recognizer.ChunkSamples <= audio.Length; start += recognizer.ChunkSamples)
            {
                Assert.NotNull(recognizer.ProcessAudio(audio.AsSpan(start, recognizer.ChunkSamples)));   // encoded and decoded
                foreach (var token in new[] { engine.LastToken, rng.Next(vocab - 1), rng.Next(vocab - 1) })
                {
                    step.Predict(token);
                    for (var f = 0; f < frames; f++)
                        CopyBits(step.JoinFrames(f, 1), single.AsSpan(f * vocab, vocab));
                    for (var first = 0; first < frames; first++)
                    {
                        for (var count = 1; first + count <= frames; count++)
                        {
                            var batch = step.JoinFrames(first, count);
                            Assert.Equal(count * vocab, batch.Length);
                            for (var i = 0; i < batch.Length; i++)
                            {
                                if (BitConverter.SingleToInt32Bits(batch[i]) != single[first * vocab + i])
                                    Assert.Fail($"{variant} {clip} chunk {start / recognizer.ChunkSamples}, token {token}: frames [{first}, {first + count}) " +
                                                $"row {i / vocab} entry {i % vocab} is {batch[i]:R}, one frame alone gives {BitConverter.Int32BitsToSingle(single[first * vocab + i]):R}");
                            }
                            rows += count;
                            runs++;
                        }
                    }
                }
                step.Predict(engine.LastToken);   // the decoder's pending prediction output, as it left it
            }
        }

        Assert.True(rows > 0);
        output.WriteLine($"{variant}: {rows} batched rows in {runs} runs bit-identical to one-frame runs ({NemotronTestEnvironment.Threads} threads)");
    }

    private void AssertStreamsMatch(string variant)
    {
        var recognizer = models.Get(variant);
        var engine = (NemotronRnntEngine)recognizer.Engine;
        var tokens = 0;
        try
        {
            foreach (var clip in StreamClips)
            {
                Assert.True(recognizer.TrySetLanguage(clip.Contains("/ru/") ? "ru" : "en"));
                var audio = NemotronTestEnvironment.LoadAudioRelative(clip);

                engine.BatchedJoint = false;
                recognizer.ResetStreamingState();
                var jointsBefore = engine.JointCalls;
                var frameByFrame = Replay.Run(recognizer, audio, 3200);   // the apps' 200 ms capture batches
                var frameByFrameRuns = engine.JointCalls - jointsBefore;

                engine.BatchedJoint = true;
                recognizer.ResetStreamingState();
                jointsBefore = engine.JointCalls;
                var batched = Replay.Run(recognizer, audio, 3200);
                var batchedRuns = engine.JointCalls - jointsBefore;

                Assert.True(frameByFrame.Positions().SequenceEqual(batched.Positions()), $"{variant} {clip}: the tokens or their positions differ");
                Assert.Equal(frameByFrame.Tokens.Select(t => BitConverter.SingleToInt32Bits(t.LogProb)), batched.Tokens.Select(t => BitConverter.SingleToInt32Bits(t.LogProb)));
                Assert.Equal(frameByFrame.Text.ToString(), batched.Text.ToString());
                Assert.True(batchedRuns * 2 < frameByFrameRuns, $"{variant} {clip}: {batchedRuns} batched joint runs, {frameByFrameRuns} frame by frame");
                tokens += batched.Tokens.Count;
                output.WriteLine($"{variant} {clip}: {batched.Tokens.Count} tokens identical, joint runs {frameByFrameRuns} -> {batchedRuns}");
            }
        }
        finally
        {
            engine.BatchedJoint = true;
            recognizer.ResetStreamingState();
        }
        Assert.True(tokens > 0);   // a quiet clip may give none (the recognizer has no gain control), the set as a whole must not
    }

    private static void CopyBits(ReadOnlySpan<float> source, Span<int> target)
    {
        Assert.Equal(target.Length, source.Length);
        for (var i = 0; i < source.Length; i++)
            target[i] = BitConverter.SingleToInt32Bits(source[i]);
    }
}
