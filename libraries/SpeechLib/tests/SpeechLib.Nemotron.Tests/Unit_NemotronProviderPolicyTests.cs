using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// Which execution provider the Nemotron encoder gets, for every kind of stored setting and every state of the machine, without a GPU:
/// WebGPU only on a discrete graphics card, never for the low-power choice, never after a crash marker, one recognizer at a time.
/// </summary>
public sealed class Unit_NemotronProviderPolicyTests
{
    // ── stored values that are not WebGPU: the CPU, quietly ─────────────

    [Theory]
    [InlineData("cpu")]
    [InlineData("CPU")]
    [InlineData(" cpu ")]
    [InlineData("follow_config")]
    [InlineData("")]
    [InlineData(null)]
    public void Decide_CpuAndFollowConfig_ShouldBeTheCpuWithoutAReasonOrAWarning(string? requested)
    {
        var rig = new WebGpuRig();

        var plan = NemotronProviderPolicy.Decide(requested, rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Equal("cpu", plan.Info.Effective);
        Assert.Null(plan.Info.Reason);
        Assert.Null(plan.Device);
        Assert.Null(plan.Lease);
        Assert.Empty(rig.Log);
        Assert.Equal(0, rig.Adapters.Queries);   // the GPU stack is not even asked
        Assert.False(rig.Gate.IsHeld);
    }

    [Theory]
    [InlineData("cuda")]
    [InlineData("dml")]
    [InlineData("tensorrt")]
    [InlineData("garbage")]
    public void Decide_OtherStoredValues_ShouldBeTheCpuWithTheReasonButNoWarning(string requested)
    {
        var rig = new WebGpuRig();

        var plan = NemotronProviderPolicy.Decide(requested, rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Equal(requested, plan.Info.Requested);
        Assert.Contains("CPU or on WebGPU only", plan.Info.Reason);
        Assert.Empty(rig.Log);   // a stale cuda/dml from the GPU builds degrades as quietly as it always did
        Assert.Null(plan.Lease);
    }

    // ── WebGPU on a discrete card ───────────────────────────────────────

    [Theory]
    [InlineData("webgpu")]
    [InlineData("webgpu:hp")]
    [InlineData("WebGPU:HP")]
    [InlineData("webgpu:high")]
    [InlineData("webgpu,power=high-performance")]
    public void Decide_WebGpuWithADiscreteCard_ShouldPutTheEncoderOnTheDiscreteCard(string requested)
    {
        var rig = new WebGpuRig();

        var plan = NemotronProviderPolicy.Decide(requested, rig.Services);

        Assert.True(plan.Info.EncoderOnWebGpu);
        Assert.Equal("webgpu", plan.Info.Effective);
        Assert.Equal(FakeAdapters.Discrete.Description, plan.Info.Adapter);
        Assert.Same(FakeAdapters.Discrete, plan.Device);   // not the integrated adapter, although it is listed first
        Assert.Null(plan.Info.Reason);
        Assert.NotNull(plan.Lease);
        Assert.True(rig.Gate.IsHeld);
        Assert.Empty(rig.Log);   // the notice that WebGPU is used is printed once the session exists, not by the decision
    }

    [Fact]
    public void Decide_WebGpuWithTheDiscreteCardListedFirst_ShouldStillFindIt()
    {
        var rig = new WebGpuRig();
        rig.Adapters.DeviceList = [FakeAdapters.Discrete with { Index = 0 }, FakeAdapters.Integrated with { Index = 1 }];

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.Equal(0, plan.Device!.Index);
    }

    [Theory]
    [InlineData("webgpu:1", "the adapter index")]
    [InlineData("webgpu:0", "the adapter index")]
    [InlineData("webgpu:hp,layout=NHWC", "the provider options")]
    [InlineData("webgpu:hp,capture=1", "the provider options")]
    [InlineData("webgpu,int64=1,maxssb=1024,cache=bucket,forcecpu=a", "the provider options")]
    [InlineData("webgpu:1,layout=NHWC", "the adapter index, the provider options")]
    [InlineData("webgpu,power=bogus", "the power preference")]
    public void Decide_IndexAndRawOptions_ShouldBeParsedButIgnoredForNemotron(string requested, string ignored)
    {
        var rig = new WebGpuRig();

        var plan = NemotronProviderPolicy.Decide(requested, rig.Services);

        Assert.True(plan.Info.EncoderOnWebGpu);
        Assert.Same(FakeAdapters.Discrete, plan.Device);   // the index does not choose the adapter
        Assert.Contains("ignored for Nemotron: " + ignored, plan.Info.Reason);
    }

    // ── WebGPU that is not used ─────────────────────────────────────────

    [Theory]
    [InlineData("webgpu:lp")]
    [InlineData("WEBGPU:LP")]
    [InlineData("webgpu:low-power")]
    [InlineData("webgpu,power=low-power")]
    public void Decide_LowPower_ShouldBeTheCpuEvenWithADiscreteCard_WithoutTouchingTheGpuStack(string requested)
    {
        var rig = new WebGpuRig();

        var plan = NemotronProviderPolicy.Decide(requested, rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Contains("integrated graphics", plan.Info.Reason);
        Assert.Contains("slower than the CPU", plan.Info.Reason);
        Assert.Single(rig.Log);
        Assert.Contains("using CPU", rig.Log[0]);
        Assert.Equal(0, rig.Adapters.Queries);
        Assert.Empty(rig.Events);
        Assert.False(rig.Gate.IsHeld);
    }

    [Theory]
    [InlineData("webgpu")]
    [InlineData("webgpu:hp")]
    public void Decide_OnlyIntegratedGraphics_ShouldBeTheCpuAndNameTheAdapters(string requested)
    {
        var rig = new WebGpuRig();
        rig.Adapters.DeviceList = [FakeAdapters.Integrated];

        var plan = NemotronProviderPolicy.Decide(requested, rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Contains("no discrete graphics card", plan.Info.Reason);
        Assert.Contains("Intel(R) Graphics: integrated", plan.Info.Reason);
        Assert.Single(rig.Log);
        Assert.Null(plan.Lease);
    }

    [Fact]
    public void Decide_AdapterThatDoesNotSayWhetherItIsDiscrete_ShouldNotBeTrusted()
    {
        var rig = new WebGpuRig();
        rig.Adapters.DeviceList = [FakeAdapters.Discrete with { IsDiscrete = null }];

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Contains("type not reported", plan.Info.Reason);
    }

    [Fact]
    public void Decide_PluginOrAdapterMissing_ShouldBeTheCpuWithThePluginsOwnReason()
    {
        var rig = new WebGpuRig();
        rig.Adapters.UnavailableReason = "WebGPU plugin library onnxruntime_providers_webgpu.dll not found next to the application";
        rig.Adapters.DeviceList = [];

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Equal(rig.Adapters.UnavailableReason, plan.Info.Reason);
        Assert.Single(rig.Log);
        Assert.Contains("not found next to the application", rig.Log[0]);
    }

    // ── crash marker ────────────────────────────────────────────────────

    [Fact]
    public void Decide_StaleCrashMarker_ShouldForceTheCpuAndSayHowToTryAgain()
    {
        var rig = new WebGpuRig();
        rig.Marker.Stale = "process 4711 ended while it was using WebGPU (marker x.marker)";

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Contains("process 4711 ended while it was using WebGPU", plan.Info.Reason);
        Assert.Contains("SPEECHLIB_WEBGPU_RETRY=1", plan.Info.Reason);
        Assert.Single(rig.Log);
        Assert.Null(plan.Lease);
        Assert.False(rig.Gate.IsHeld);
        Assert.Equal(0, rig.Marker.Cleared);   // the marker keeps WebGPU off for every start until it is cleared on purpose
    }

    [Fact]
    public void Decide_StaleCrashMarkerAndTheRetryVariable_ShouldClearItAndUseWebGpu()
    {
        var rig = new WebGpuRig();
        rig.Marker.Stale = "process 4711 ended while it was using WebGPU";
        rig.Environment[NemotronProviderPolicy.RetryVariable] = "1";

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.True(plan.Info.EncoderOnWebGpu);
        Assert.Equal(1, rig.Marker.Cleared);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("")]
    public void Decide_RetryVariableThatIsNotOne_ShouldNotClearTheMarker(string? value)
    {
        var rig = new WebGpuRig();
        rig.Marker.Stale = "process 4711 ended while it was using WebGPU";
        rig.Environment[NemotronProviderPolicy.RetryVariable] = value;

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Equal(0, rig.Marker.Cleared);
    }

    // ── one WebGPU recognizer per process ───────────────────────────────

    [Fact]
    public void Decide_SecondWebGpuRecognizer_ShouldGetTheCpu_UntilTheFirstIsDisposed()
    {
        var rig = new WebGpuRig();

        var first = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);
        var second = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.True(first.Info.EncoderOnWebGpu);
        Assert.False(second.Info.EncoderOnWebGpu);
        Assert.Contains("another recognizer in this process already uses WebGPU", second.Info.Reason);
        Assert.Single(rig.Log);

        first.Lease!.Dispose();
        var third = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);
        Assert.True(third.Info.EncoderOnWebGpu);
    }

    [Fact]
    public void Decide_AfterAWebGpuFailureInThisProcess_ShouldGetTheCpu()
    {
        var rig = new WebGpuRig();
        rig.Gate.ReportFailure("EP_FAIL");

        var plan = NemotronProviderPolicy.Decide("webgpu:hp", rig.Services);

        Assert.False(plan.Info.EncoderOnWebGpu);
        Assert.Contains("WebGPU failed earlier in this process (EP_FAIL)", plan.Info.Reason);
    }

    // ── what the report says ────────────────────────────────────────────

    [Fact]
    public void Summary_ShouldNameTheProvidersTheAdapterAndTheReason()
    {
        var onGpu = new NemotronProviderInfo("webgpu:hp", true, "NVIDIA GeForce", null);
        var onCpu = new NemotronProviderInfo("webgpu:lp", false, null, "integrated graphics run Nemotron about 2x slower than the CPU");

        Assert.Equal("encoder on WebGPU (NVIDIA GeForce), prediction network and joint on CPU (requested: webgpu:hp)", onGpu.Summary);
        Assert.Equal("all graphs on CPU (requested: webgpu:lp; integrated graphics run Nemotron about 2x slower than the CPU)", onCpu.Summary);
        Assert.Equal("all graphs on CPU (requested: none)", NemotronProviderInfo.Cpu(null).Summary);
    }
}
