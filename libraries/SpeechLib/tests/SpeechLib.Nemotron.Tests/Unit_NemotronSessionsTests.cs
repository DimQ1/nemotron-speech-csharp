using Microsoft.ML.OnnxRuntime;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The guarded creation of the three sessions (provider per graph, retry on the CPU, crash marker, gate), on a model folder whose three
/// graphs are the same tiny identity model and with the plugin, marker and gate replaced: no GPU and no Nemotron model needed.
/// </summary>
public sealed class Unit_NemotronSessionsTests : IDisposable
{
    private static readonly NemotronModelConfig Config = NemotronTestEnvironment.LoadConfig("int4-c056");

    private readonly string _folder = TinyOnnxModel.CreateModelFolder(Config);
    private readonly WebGpuRig _rig = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private NemotronSessions Create(string provider, int threads = 2) =>
        NemotronSessions.Create(_folder, Config, new NemotronEngineOptions(provider, threads, Services: _rig.Services));

    private static string Encoder => Config.Encoder.FileName;
    private static string Decoder => Config.Decoder.FileName;
    private static string Joiner => Config.Joiner.FileName;

    private static OnnxRuntimeException RealOnnxRuntimeException()
    {
        try
        {
            new InferenceSession([1, 2, 3]).Dispose();
        }
        catch (OnnxRuntimeException e)
        {
            return e;
        }
        throw new InvalidOperationException("ONNX Runtime accepted a model of three bytes.");
    }

    // ── one provider per graph ──────────────────────────────────────────

    [Fact]
    public void Create_Cpu_ShouldPutEveryGraphOnTheCpuAndTouchNeitherMarkerNorGate()
    {
        using var sessions = Create("cpu");

        Assert.Equal([(Encoder, false), (Decoder, false), (Joiner, false)], _rig.Created);
        Assert.False(sessions.Provider.EncoderOnWebGpu);
        Assert.Empty(_rig.Log);
        Assert.Equal(0, _rig.Marker.Armed);
        Assert.False(_rig.Gate.IsHeld);
        Assert.Empty(_rig.Adapters.Applied);
    }

    [Theory]
    [InlineData("webgpu:hp")]
    [InlineData("webgpu")]
    [InlineData("webgpu:1,layout=NHWC")]
    public void Create_WebGpu_ShouldGiveOnlyTheEncoderTheProvider_AndArmTheMarkerBeforeItsSessionExists(string requested)
    {
        using var sessions = Create(requested);

        Assert.Equal([(Encoder, true), (Decoder, false), (Joiner, false)], _rig.Created);
        Assert.Same(FakeAdapters.Discrete, Assert.Single(_rig.Adapters.Applied));
        // appended to the encoder's options, then the marker, then the session: a crash while it is created leaves the marker
        Assert.Equal(["apply", "arm", $"create {Encoder} webgpu", $"create {Decoder}", $"create {Joiner}"], _rig.Events);
        Assert.True(sessions.Provider.EncoderOnWebGpu);
        Assert.True(_rig.Gate.IsHeld);
        Assert.Contains("encoder on WebGPU (NVIDIA GeForce RTX 5070 Ti Laptop GPU), prediction network and joint on CPU", Assert.Single(_rig.Log.Where(l => !l.Contains("Warning"))));
    }

    [Fact]
    public void Create_WebGpuWithIgnoredOptions_ShouldWarnOnce()
    {
        using var sessions = Create("webgpu:1,capture=1");

        Assert.Single(_rig.Log, l => l.Contains("Warning") && l.Contains("ignored for Nemotron"));
    }

    [Theory]
    [InlineData("webgpu:lp")]
    [InlineData("cuda")]
    public void Create_RequestsThatMeanTheCpu_ShouldCreateAllGraphsOnTheCpu(string requested)
    {
        using var sessions = Create(requested);

        Assert.Equal([(Encoder, false), (Decoder, false), (Joiner, false)], _rig.Created);
        Assert.False(sessions.Provider.EncoderOnWebGpu);
        Assert.Equal(requested, sessions.Provider.Requested);
        Assert.Equal(0, _rig.Marker.Armed);
    }

    // ── a WebGPU session that cannot be created ─────────────────────────

    [Fact]
    public void Create_EncoderSessionFailsOnWebGpu_ShouldRetryOnTheCpuWithOneWarning_AndCloseWebGpuForTheProcess()
    {
        var failure = RealOnnxRuntimeException();   // what new InferenceSession throws when the plugin cannot create its provider
        _rig.Fail = (_, webGpu) => webGpu ? failure : null;

        using var sessions = Create("webgpu:hp");

        Assert.Equal([(Encoder, false), (Decoder, false), (Joiner, false)], _rig.Created);   // the encoder was created again, on the CPU
        Assert.False(sessions.Provider.EncoderOnWebGpu);
        Assert.Equal("webgpu:hp", sessions.Provider.Requested);
        Assert.Contains(failure.Message, sessions.Provider.Reason);
        var warning = Assert.Single(_rig.Log);
        Assert.Contains("Warning: WebGPU failed for the Nemotron encoder", warning);
        Assert.Contains(failure.Message, warning);
        Assert.Contains("using CPU", warning);
        Assert.Equal(["apply", "arm", $"create {Encoder} webgpu", "disarm", $"create {Encoder}", $"create {Decoder}", $"create {Joiner}"], _rig.Events);
        Assert.False(_rig.Gate.IsHeld);
        Assert.Null(_rig.Gate.TryAcquire(out var refusal));   // the next recognizer does not try the same thing again
        Assert.Contains(failure.Message, refusal);
    }

    [Fact]
    public void Create_ProviderCannotBeAppended_ShouldUseTheCpuWithoutEverArmingTheMarker()
    {
        _rig.Adapters.ApplyResult = false;

        using var sessions = Create("webgpu:hp");

        Assert.Equal([(Encoder, false), (Decoder, false), (Joiner, false)], _rig.Created);
        Assert.False(sessions.Provider.EncoderOnWebGpu);
        Assert.Equal(0, _rig.Marker.Armed);
        Assert.Contains("could not be appended", sessions.Provider.Reason);
        Assert.Single(_rig.Log);
        Assert.False(_rig.Gate.IsHeld);
    }

    [Fact]
    public void Create_CpuSessionFails_ShouldPropagate_AndReleaseMarkerAndGate()
    {
        _rig.Fail = (graph, _) => graph == Joiner ? new InvalidDataException("joint is damaged") : null;

        var error = Assert.Throws<InvalidDataException>(() => Create("webgpu:hp"));

        Assert.Equal("joint is damaged", error.Message);
        Assert.False(_rig.Gate.IsHeld);
        Assert.Equal(_rig.Marker.Armed, _rig.Marker.Disarmed);   // a clean failure is not a crash
    }

    [Fact]
    public void Create_CpuEncoderFails_ShouldPropagateAndNotBeRetried()
    {
        _rig.Fail = (graph, _) => graph == Encoder ? new InvalidDataException("encoder is damaged") : null;

        var error = Assert.Throws<InvalidDataException>(() => Create("cpu"));

        Assert.Equal("encoder is damaged", error.Message);
        Assert.Empty(_rig.Created);
    }

    [Fact]
    public void Create_NonPositiveThreads_ShouldThrowBeforeAnythingIsAcquired()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create("webgpu:hp", threads: 0));

        Assert.False(_rig.Gate.IsHeld);
        Assert.Empty(_rig.Events);
    }

    // ── crash marker lifecycle ──────────────────────────────────────────

    [Fact]
    public void EncoderRan_ShouldRemoveTheMarkerOnlyAfterTheEncoderProvedItselfFourTimes()
    {
        using var sessions = Create("webgpu:hp");

        for (var run = 1; run < NemotronSessions.RunsUntilProven; run++)
        {
            sessions.EncoderRan();
            Assert.Equal(0, _rig.Marker.Disarmed);
        }
        sessions.EncoderRan();
        Assert.Equal(1, _rig.Marker.Disarmed);
        sessions.EncoderRan();
        sessions.EncoderRan();
        Assert.Equal(1, _rig.Marker.Disarmed);   // once
        Assert.True(_rig.Gate.IsHeld);           // the gate stays with the recognizer until it is disposed
    }

    [Fact]
    public void EncoderRan_OnTheCpu_ShouldDoNothing()
    {
        using var sessions = Create("cpu");

        for (var run = 0; run < 10; run++)
            sessions.EncoderRan();

        Assert.Equal(0, _rig.Marker.Disarmed);
    }

    [Fact]
    public void Dispose_ShouldRemoveTheMarkerAndGiveTheGateBack()
    {
        var sessions = Create("webgpu:hp");

        sessions.Dispose();

        Assert.Equal(1, _rig.Marker.Disarmed);
        Assert.False(_rig.Gate.IsHeld);
        sessions.Dispose();   // twice is fine
        Assert.Equal(1, _rig.Marker.Disarmed);
    }

    [Fact]
    public void Dispose_AfterTheMarkerWasRemoved_ShouldNotRemoveItAgain()
    {
        var sessions = Create("webgpu:hp");
        for (var run = 0; run < NemotronSessions.RunsUntilProven; run++)
            sessions.EncoderRan();

        sessions.Dispose();

        Assert.Equal(1, _rig.Marker.Disarmed);
    }

    // ── a run that failed on WebGPU ─────────────────────────────────────

    [Fact]
    public void ReplaceEncoderWithCpu_ShouldSwapTheEncoder_ReportTheReason_AndCloseWebGpu()
    {
        using var sessions = Create("webgpu:hp");
        var before = sessions.Encoder;
        _rig.Log.Clear();
        _rig.Events.Clear();

        sessions.ReplaceEncoderWithCpu("a run failed: device lost");

        Assert.NotSame(before, sessions.Encoder);
        Assert.NotNull(sessions.Encoder.InputMetadata);
        Assert.False(sessions.Provider.EncoderOnWebGpu);
        Assert.Equal("webgpu:hp", sessions.Provider.Requested);
        Assert.Equal("a run failed: device lost", sessions.Provider.Reason);
        Assert.Equal([$"create {Encoder}", "disarm"], _rig.Events);
        Assert.Equal(1, _rig.Created.Count(c => c == (Encoder, false)));
        Assert.Contains("device lost", Assert.Single(_rig.Log));
        Assert.False(_rig.Gate.IsHeld);
        Assert.Null(_rig.Gate.TryAcquire(out var refusal));
        Assert.Contains("device lost", refusal);
    }

    [Fact]
    public void ReplaceEncoderWithCpu_WhenTheCpuSessionCannotBeCreated_ShouldThrowAndChangeNothing()
    {
        using var sessions = Create("webgpu:hp");
        var before = sessions.Encoder;
        _rig.Fail = (_, webGpu) => webGpu ? null : new IOException("out of memory");

        Assert.Throws<IOException>(() => sessions.ReplaceEncoderWithCpu("a run failed"));

        Assert.Same(before, sessions.Encoder);
        Assert.True(sessions.Provider.EncoderOnWebGpu);
        Assert.True(_rig.Gate.IsHeld);
    }
}
