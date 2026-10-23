using Microsoft.ML.OnnxRuntime;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The engine on the real INT4 model with a fake WebGPU (an adapter list that reports a discrete card; the "WebGPU" session is the CPU
/// session the factory builds, so no GPU is touched in this process): the split of providers, the warm-up run, the crash marker over the
/// first chunks, and the move to the CPU after a failed run. The text must be the CPU engine's, token for token.
/// </summary>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronProviderEngineTests
{
    // These tests load engines of their own: give the cached models back first.
    public E2E_NemotronProviderEngineTests(NemotronModelCache models) => models.ReleaseAll();

    private static string ModelFolder => NemotronTestEnvironment.LocateModel("int4-c056")!;

    private static NemotronRnntEngine CreateEngine(string provider, WebGpuRig? rig = null) =>
        NemotronRnntEngine.Create(ModelFolder, NemotronModelConfig.Load(ModelFolder),
            new NemotronEngineOptions(provider, NemotronTestEnvironment.Threads, Services: rig?.Services));

    private static float[] Clip() => NemotronTestEnvironment.LoadAudioRelative("cv17/en/0001.wav");

    /// <summary>Decodes the clip chunk by chunk (the last one zero-padded) from chunk <paramref name="first"/> up to <paramref name="end"/>.</summary>
    private static List<NemotronToken> Decode(NemotronRnntEngine engine, float[] audio, int first = 0, int end = int.MaxValue)
    {
        var size = engine.ChunkSamples;
        var chunk = new float[size];
        var tokens = new List<NemotronToken>();
        for (var index = first; index < end && index * size < audio.Length; index++)
        {
            Array.Clear(chunk);
            audio.AsSpan(index * size, Math.Min(size, audio.Length - index * size)).CopyTo(chunk);
            engine.DecodeChunk(chunk, default, index, tokens);
        }
        return tokens;
    }

    private static List<NemotronToken> CpuReference(float[] audio)
    {
        using var cpu = CreateEngine("cpu");
        return Decode(cpu, audio);
    }

    [NemotronModelFact]
    public void FakeWebGpu_ShouldGiveOnlyTheEncoderTheProvider_AndReproduceTheCpuTokens()
    {
        var audio = Clip();
        var expected = CpuReference(audio);
        var rig = new WebGpuRig();
        var config = NemotronModelConfig.Load(ModelFolder);

        List<NemotronToken> tokens;
        using (var engine = CreateEngine("webgpu:hp", rig))
        {
            Assert.Equal([(config.Encoder.FileName, true), (config.Decoder.FileName, false), (config.Joiner.FileName, false)], rig.Created);
            Assert.True(engine.Provider.EncoderOnWebGpu);
            Assert.Equal(FakeAdapters.Discrete.Description, engine.Provider.Adapter);
            Assert.True(rig.Gate.IsHeld);

            tokens = Decode(engine, audio);
        }

        Assert.NotEmpty(expected);
        Assert.Equal(expected, tokens);   // the warm-up run before the first chunk leaves the stream untouched
        Assert.False(rig.Gate.IsHeld);
        Assert.Equal(1, rig.Marker.Armed);
        Assert.Equal(1, rig.Marker.Disarmed);
    }

    [NemotronModelFact]
    public void FakeWebGpu_CrashMarker_ShouldStayUntilTheWarmUpAndThreeChunksWentThrough()
    {
        var audio = Clip();
        var rig = new WebGpuRig();
        using var engine = CreateEngine("webgpu:hp", rig);

        Assert.Equal(1, rig.Marker.Armed);   // armed before the encoder session was created (see Unit_NemotronSessionsTests for the order)
        Assert.Equal(0, rig.Marker.Disarmed);    // the warm-up run counts as the first of four runs

        Decode(engine, audio, first: 0, end: 2);
        Assert.Equal(0, rig.Marker.Disarmed);

        Decode(engine, audio, first: 2, end: 3);
        Assert.Equal(1, rig.Marker.Disarmed);    // warm-up + chunks 0, 1, 2
        Assert.True(rig.Gate.IsHeld);            // the gate stays with the engine
    }

    [NemotronModelFact]
    public void FakeWebGpu_ARunThatFails_ShouldFaultTheChunk_AndTheNextResetShouldMoveTheEncoderToTheCpu()
    {
        var audio = Clip();
        var expected = CpuReference(audio);
        var rig = new WebGpuRig();
        var config = NemotronModelConfig.Load(ModelFolder);
        using var engine = CreateEngine("webgpu:hp", rig);
        Decode(engine, audio, first: 0, end: 1);

        engine.EncoderRunOptions.Terminate = true;   // what a lost device does to the next run: it throws
        Assert.Throws<OnnxRuntimeException>(() => Decode(engine, audio, first: 1, end: 2));
        engine.EncoderRunOptions.Terminate = false;

        Assert.True(engine.Provider.EncoderOnWebGpu);   // nothing changes until the stream is reset: it is undefined until then anyway
        Assert.Equal(0, rig.Marker.Disarmed);
        rig.Log.Clear();

        engine.Reset();

        Assert.False(engine.Provider.EncoderOnWebGpu);
        Assert.Equal("webgpu:hp", engine.Provider.Requested);
        Assert.Contains("a run failed", engine.Provider.Reason);
        Assert.Equal((config.Encoder.FileName, false), rig.Created[^1]);
        Assert.Equal(1, rig.Marker.Disarmed);
        Assert.False(rig.Gate.IsHeld);
        Assert.Null(rig.Gate.TryAcquire(out _));   // WebGPU is closed for the rest of the process
        Assert.Contains("using CPU", Assert.Single(rig.Log));
        Assert.Equal(expected, Decode(engine, audio));   // and the whole clip decodes as on the CPU
    }

    [NemotronModelFact]
    public void FakeWebGpu_SecondEngineAtTheSameTime_ShouldRunOnTheCpu()
    {
        var audio = Clip();
        var expected = CpuReference(audio);
        var rig = new WebGpuRig();
        using var first = CreateEngine("webgpu:hp", rig);

        using var second = CreateEngine("webgpu:hp", rig);

        Assert.True(first.Provider.EncoderOnWebGpu);
        Assert.False(second.Provider.EncoderOnWebGpu);
        Assert.Contains("another recognizer in this process already uses WebGPU", second.Provider.Reason);
        Assert.Equal(expected, Decode(second, audio));
        Assert.Equal(1, rig.Created.Count(c => c.WebGpu));   // one WebGPU session in total
    }

    [NemotronModelFact]
    public void FakeWebGpu_AStaleCrashMarker_ShouldKeepTheEncoderOnTheCpu()
    {
        var audio = Clip();
        var rig = new WebGpuRig();
        rig.Marker.Stale = "process 4711 ended while it was using WebGPU";

        using var engine = CreateEngine("webgpu:hp", rig);

        Assert.False(engine.Provider.EncoderOnWebGpu);
        Assert.Contains("process 4711 ended while it was using WebGPU", engine.Provider.Reason);
        Assert.Equal(0, rig.Marker.Armed);
        Assert.NotEmpty(Decode(engine, audio));
    }

    [NemotronModelFact]
    public void Recognizer_LowPower_ShouldRunOnTheCpuAndSayWhy_WithoutTouchingTheGpuStack()
    {
        // webgpu:lp is decided before the plugin is asked, so this is safe in the test process even on a machine with a GPU.
        using var recognizer = new NemotronUtteranceRecognizer(ModelFolder, "webgpu:lp", langId: null, useVad: false,
            new NemotronRecognizerOptions { CpuThreads = NemotronTestEnvironment.Threads });

        var info = recognizer.ExecutionProvider;

        Assert.False(info.EncoderOnWebGpu);
        Assert.Equal("webgpu:lp", info.Requested);
        Assert.Contains("integrated graphics", info.Reason);
        Assert.Same(info, recognizer.Engine.ExecutionProvider);
    }
}
