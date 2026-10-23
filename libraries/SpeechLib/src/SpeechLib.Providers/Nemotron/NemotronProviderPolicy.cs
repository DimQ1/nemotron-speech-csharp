using Microsoft.ML.OnnxRuntime;

namespace SpeechLib.Nemotron;

/// <summary>
/// The outside world of the provider choice and of session creation, injectable so that the policy, the guarded creation and the
/// crash marker run in tests without a GPU, a plugin or a model. Production code uses <see cref="Default"/>.
/// </summary>
internal sealed class NemotronProviderServices
{
    /// <summary>The plugin that ships with the application, the marker files of the user, the gate of the process, the console.</summary>
    public static NemotronProviderServices Default { get; } = new();

    /// <summary>The graphics adapters on offer.</summary>
    public IWebGpuAdapters Adapters { get; init; } = PluginWebGpuAdapters.Instance;

    /// <summary>Remembers a process that dies while it uses WebGPU.</summary>
    public IWebGpuCrashMarker Marker { get; init; } = WebGpuCrashMarker.Default;

    /// <summary>Lets one recognizer at a time use WebGPU.</summary>
    public WebGpuProcessGate Gate { get; init; } = WebGpuProcessGate.Shared;

    /// <summary>
    /// Creates a session from a model file; the flag says whether <c>options</c> carry the WebGPU provider (a test fails exactly those).
    /// </summary>
    public Func<string, SessionOptions, bool, InferenceSession> CreateSession { get; init; } = (path, options, _) => new InferenceSession(path, options);

    /// <summary>Receives the one-line warnings and notices (default: the console, indented like the other providers' warnings).</summary>
    public Action<string> Log { get; init; } = Console.WriteLine;

    /// <summary>Reads an environment variable.</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;
}

/// <summary>What the policy decided: the report, and for WebGPU the adapter to use and the lease that makes this recognizer the one WebGPU user of the process.</summary>
internal sealed record NemotronProviderPlan(NemotronProviderInfo Info, WebGpuDeviceInfo? Device, WebGpuProcessGate.Lease? Lease);

/// <summary>
/// Which execution provider the Nemotron encoder gets. The prediction network and the joint never leave the CPU (measured on an
/// RTX 5070 Ti laptop: 7x and 2x slower per call on WebGPU than on the CPU, the text is the same). The encoder runs on WebGPU only when
/// every condition holds, and on the CPU otherwise, with the reason in <see cref="NemotronProviderInfo.Reason"/>:
/// <list type="number">
/// <item>the request names WebGPU (<c>webgpu</c>, <c>webgpu:hp</c>, <c>webgpu:lp</c>, <c>webgpu:&lt;index&gt;</c>, with or without options);
/// every other value (<c>cpu</c>, <c>follow_config</c>, <c>cuda</c>, <c>dml</c>, unknown) is the CPU;</item>
/// <item>it is not the low-power choice: <c>lp</c> is the CPU, because the integrated GPU measured 2.2x slower than the CPU;</item>
/// <item>the plugin is installed and finds an adapter;</item>
/// <item>one of the adapters is a discrete graphics card (integrated graphics are never used);</item>
/// <item>no earlier process died while using WebGPU (<see cref="IWebGpuCrashMarker"/>), unless re-enabled;</item>
/// <item>no other recognizer of this process uses WebGPU and WebGPU did not fail earlier in this process (<see cref="WebGpuProcessGate"/>).</item>
/// </list>
/// An adapter index and raw provider options are still parsed, so that every stored setting stays valid, but are ignored: the
/// adapter is the discrete card with the high-performance power preference and the plugin's defaults (measured: the index has no
/// effect on the adapter that runs, <c>enableInt64</c> crashes the process, <c>enableGraphCapture</c> is rejected, no other option
/// gains more than 10 percent).
/// </summary>
internal static class NemotronProviderPolicy
{
    /// <summary>Set to <c>1</c> for one start to try WebGPU again after a crash marker of an earlier run kept it off.</summary>
    internal const string RetryVariable = "SPEECHLIB_WEBGPU_RETRY";

    /// <summary>Decides for one recognizer. A returned lease belongs to the caller, who must dispose it.</summary>
    public static NemotronProviderPlan Decide(string? requested, NemotronProviderServices services)
    {
        var request = WebGpuRequest.TryParse(requested);
        if (request is null)
            return Cpu(requested, IsPlainCpuName(requested) ? null : "the Nemotron engine runs on the CPU or on WebGPU only");

        if (request.PowerPreference == "low-power")
            return Refuse(requested!, "integrated graphics run Nemotron about 2x slower than the CPU", services);

        var adapters = services.Adapters;
        if (adapters.UnavailableReason is { } unavailable)
            return Refuse(requested!, unavailable, services);

        var devices = adapters.Devices;
        var discrete = devices.FirstOrDefault(d => d.IsDiscrete == true);
        if (discrete is null)
            return Refuse(requested!, $"no discrete graphics card found ({DescribeAll(devices)}); integrated graphics run Nemotron slower than the CPU", services);

        if (string.Equals(services.GetEnvironmentVariable(RetryVariable), "1", StringComparison.Ordinal))
            services.Marker.ClearStale();
        if (services.Marker.FindStale() is { } stale)
            return Refuse(requested!, $"{stale}, so WebGPU stays off until it is re-enabled (delete the marker file or start once with {RetryVariable}=1)", services);

        var lease = services.Gate.TryAcquire(out var refusal);
        if (lease is null)
            return Refuse(requested!, refusal!, services);

        var info = new NemotronProviderInfo(requested!, true, discrete.Description ?? discrete.Vendor, IgnoredOptions(request));
        return new NemotronProviderPlan(info, discrete, lease);
    }

    private static NemotronProviderPlan Cpu(string? requested, string? reason) =>
        new(NemotronProviderInfo.Cpu(requested, reason), null, null);

    private static NemotronProviderPlan Refuse(string requested, string reason, NemotronProviderServices services)
    {
        services.Log($"  Warning: WebGPU not used for the Nemotron encoder ({reason}); using CPU.");
        return Cpu(requested, reason);
    }

    private static bool IsPlainCpuName(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ||
        requested.Trim().Equals("cpu", StringComparison.OrdinalIgnoreCase) ||
        requested.Trim().Equals("follow_config", StringComparison.OrdinalIgnoreCase);

    private static string DescribeAll(IReadOnlyList<WebGpuDeviceInfo> devices) =>
        string.Join(", ", devices.Select(d =>
            $"{d.Description ?? d.Vendor}: {d.IsDiscrete switch { true => "discrete", false => "integrated", _ => "type not reported" }}"));

    private static string? IgnoredOptions(WebGpuRequest request)
    {
        var ignored = new List<string>();
        if (request.DeviceIndex is not null)
            ignored.Add("the adapter index");
        if (request.PowerPreference is not null and not "high-performance")
            ignored.Add("the power preference");
        if (request with { DeviceIndex = null, PowerPreference = null } != new WebGpuRequest())
            ignored.Add("the provider options");

        return ignored.Count == 0
            ? null
            : $"ignored for Nemotron: {string.Join(", ", ignored)}; the encoder uses the discrete graphics card with the default options";
    }
}
