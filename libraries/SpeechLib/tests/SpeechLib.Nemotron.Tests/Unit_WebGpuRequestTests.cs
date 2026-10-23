using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The stored provider strings the apps and old settings files carry must keep parsing (the Nemotron policy ignores most of what they
/// say, but never rejects them), and the adapter description shown in logs and <c>WerEval --list-gpus</c>.
/// </summary>
public sealed class Unit_WebGpuRequestTests
{
    [Theory]
    [InlineData("webgpu", null, null)]
    [InlineData("WebGPU", null, null)]
    [InlineData("webgpu:hp", null, "high-performance")]
    [InlineData("webgpu:lp", null, "low-power")]
    [InlineData("webgpu:high-performance", null, "high-performance")]
    [InlineData("webgpu:low", null, "low-power")]
    [InlineData("webgpu:0", 0, null)]
    [InlineData("webgpu:1", 1, null)]
    [InlineData("webgpu:1,power=low-power", 1, "low-power")]
    public void TryParse_StoredValues_ShouldKeepTheirMeaning(string stored, int? index, string? power)
    {
        var request = WebGpuRequest.TryParse(stored);

        Assert.NotNull(request);
        Assert.Equal(index, request.DeviceIndex);
        Assert.Equal(power, request.PowerPreference);
    }

    [Theory]
    [InlineData("cpu")]
    [InlineData("cuda")]
    [InlineData("follow_config")]
    [InlineData("webgpux")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_OtherValues_ShouldNotBeAWebGpuRequest(string? stored)
    {
        Assert.Null(WebGpuRequest.TryParse(stored));
        Assert.False(WebGpuRequest.IsWebGpuRequest(stored));
    }

    [Fact]
    public void TryParse_RawOptions_ShouldStillBeParsed_SoOldSettingsNeverBreak()
    {
        var request = WebGpuRequest.TryParse("webgpu:0,layout=NHWC,capture=1,int64=1,cache=bucket,maxssb=1024,forcecpu=a");

        Assert.NotNull(request);
        Assert.Equal("NHWC", request.PreferredLayout);
        Assert.True(request.EnableGraphCapture);
        Assert.True(request.EnableInt64);
        Assert.Equal("bucket", request.BufferCacheMode);
        Assert.Equal(1024, request.MaxStorageBufferBindingSize);
        Assert.Equal("a", request.ForceCpuNodeNames);
    }

    [Fact]
    public void Describe_ShouldNameTheAdapterAndSayWhetherItIsDiscrete()
    {
        Assert.Equal("[1] NVIDIA GeForce RTX 5070 Ti Laptop GPU (vendor 0x10DE, device 0x2F18, discrete)", WebGpuExecutionProvider.Describe(FakeAdapters.Discrete));
        Assert.Equal("[0] Intel(R) Graphics (vendor 0x8086, device 0x7D67, integrated)", WebGpuExecutionProvider.Describe(FakeAdapters.Integrated));
        Assert.Equal("[2] AMD (vendor 0x1002, device 0x744C)",
            WebGpuExecutionProvider.Describe(new WebGpuDeviceInfo(2, "WebGpuExecutionProvider", "AMD", 0x1002, 0x744C)));
    }
}
