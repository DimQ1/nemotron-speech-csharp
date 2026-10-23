using Microsoft.ML.OnnxRuntime;

namespace SpeechLib.Nemotron;

/// <summary>The graphics adapters the WebGPU plugin offers and the way to put a session on one: the part of
/// <see cref="WebGpuExecutionProvider"/> the Nemotron policy needs, behind a seam so the policy runs without a GPU.</summary>
internal interface IWebGpuAdapters
{
    /// <summary>Why WebGPU cannot be used at all (plugin library missing, no adapter, ONNX Runtime too old), or null when at least one adapter is usable.</summary>
    string? UnavailableReason { get; }

    /// <summary>The adapters in discovery order (not stable between machines or runs).</summary>
    IReadOnlyList<WebGpuDeviceInfo> Devices { get; }

    /// <summary>Appends the WebGPU provider to <paramref name="options"/> with the high-performance power preference, for <paramref name="device"/>.</summary>
    /// <returns>False with the reason in <paramref name="error"/> when the provider could not be appended.</returns>
    bool TryApplyHighPerformance(SessionOptions options, WebGpuDeviceInfo device, out string? error);
}

/// <summary><see cref="IWebGpuAdapters"/> over the plugin that ships with the application.</summary>
internal sealed class PluginWebGpuAdapters : IWebGpuAdapters
{
    /// <summary>The one instance.</summary>
    public static PluginWebGpuAdapters Instance { get; } = new();

    private PluginWebGpuAdapters()
    {
    }

    /// <inheritdoc />
    public string? UnavailableReason => WebGpuExecutionProvider.IsAvailable ? null : WebGpuExecutionProvider.LastError ?? "WebGPU is not available";

    /// <inheritdoc />
    public IReadOnlyList<WebGpuDeviceInfo> Devices => WebGpuExecutionProvider.GetDevices();

    /// <inheritdoc />
    /// <remarks>
    /// The adapter is chosen by Dawn from the power preference, not by the device given to ONNX Runtime (measured: the index has
    /// no effect, "high-performance" runs on the discrete card). The discrete device is passed anyway so that the plugin's
    /// device list and the request agree.
    /// </remarks>
    public bool TryApplyHighPerformance(SessionOptions options, WebGpuDeviceInfo device, out string? error) =>
        WebGpuExecutionProvider.TryApply(options, new WebGpuRequest { DeviceIndex = device.Index, PowerPreference = "high-performance" }, out error);
}
