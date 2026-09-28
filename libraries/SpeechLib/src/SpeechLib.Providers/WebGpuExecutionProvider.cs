using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace SpeechLib;

/// <summary>
/// Runtime registration and adapter discovery for the WebGPU plugin execution
/// provider (<c>onnxruntime_providers_webgpu</c>).
/// </summary>
/// <remarks>
/// <para>
/// WebGPU ships as a <b>plugin</b> EP: a standalone native library that is
/// registered against the process-wide <see cref="OrtEnv"/> at runtime. The core
/// ONNX Runtime install stays untouched, so enabling WebGPU needs neither a
/// rebuild nor a GPU-specific ORT package. The plugin version is versioned
/// independently of ONNX Runtime (plugin 0.4.0 pairs with ONNX Runtime 1.30.0)
/// and must be re-picked when the core is bumped.
/// </para>
/// <para>
/// Adapter selection: the WebGPU EP asks Dawn for an adapter via
/// <c>requestAdapter</c> with a power preference hint and no explicit adapter
/// identity, so the only supported levers are
/// <see cref="WebGpuRequest.PowerPreference"/> ("high-performance" steers to the
/// discrete GPU, "low-power" to the integrated one) and choosing between the
/// <c>OrtEpDevice</c> instances the plugin advertises for each GPU hardware
/// device. Deterministic per-adapter binding would require supplying a Dawn
/// instance/device through the <c>webgpuInstance</c>/<c>webgpuDevice</c>
/// provider options, which is not implemented here.
/// </para>
/// </remarks>
public static class WebGpuExecutionProvider
{
    /// <summary>Handle the plugin library is registered under (must be unique per process).</summary>
    private const string RegistrationName = "webgpu_ep";

    /// <summary>Environment variable that overrides plugin discovery (useful for experiments).</summary>
    private const string PluginPathEnvVar = "SPEECHLIB_WEBGPU_PLUGIN";

    private static readonly object Gate = new();
    private static bool _registrationAttempted;
    private static string? _registrationError;
    private static List<DiscoveredDevice> _devices = [];

    /// <summary>Reason the WebGPU provider is unavailable, or <see langword="null"/> when it is usable.</summary>
    public static string? LastError
    {
        get
        {
            EnsureRegistered();
            return _registrationError;
        }
    }

    /// <summary>True when the plugin registered and at least one WebGPU adapter was discovered.</summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureRegistered();
            return _devices.Count > 0;
        }
    }

    /// <summary>
    /// Adapters the plugin advertises, in discovery order. The index is what
    /// <c>webgpu:&lt;index&gt;</c> selects.
    /// </summary>
    public static IReadOnlyList<WebGpuDeviceInfo> GetDevices()
    {
        EnsureRegistered();
        return _devices.Select(d => d.Info).ToList();
    }

    /// <summary>
    /// Registers the plugin library with the current <see cref="OrtEnv"/> and
    /// enumerates its devices. Idempotent; safe to call from any thread.
    /// </summary>
    /// <param name="pluginPath">
    /// Explicit path to the plugin library. When <see langword="null"/> the
    /// <c>SPEECHLIB_WEBGPU_PLUGIN</c> environment variable and the conventional
    /// <c>runtimes/&lt;rid&gt;/native</c> locations next to the application are probed.
    /// </param>
    /// <param name="error">Failure reason when the method returns <see langword="false"/>.</param>
    public static bool TryRegister(out string? error, string? pluginPath = null)
    {
        EnsureRegistered(pluginPath);
        lock (Gate)
        {
            error = _registrationError;
            return _devices.Count > 0;
        }
    }

    /// <summary>Human-readable description of a discovered adapter, for settings UIs.</summary>
    public static string Describe(WebGpuDeviceInfo device) =>
        $"[{device.Index}] {device.Vendor} (vendor 0x{device.VendorId:X4}, device 0x{device.DeviceId:X4})";

    /// <summary>
    /// Appends the WebGPU provider to plain ONNX Runtime session options using the
    /// adapter described by <paramref name="request"/>.
    /// </summary>
    /// <returns><see langword="false"/> when the plugin or the requested adapter is unavailable.</returns>
    public static bool TryApply(SessionOptions sessionOptions, WebGpuRequest request, out string? error)
    {
        if (!TryRegister(out error))
            return false;

        var target = Resolve(request.DeviceIndex);
        if (target is null)
        {
            error = request.DeviceIndex is null
                ? "no WebGPU device discovered"
                : $"WebGPU device index {request.DeviceIndex} out of range ({_devices.Count} available)";
            return false;
        }

        try
        {
            sessionOptions.AppendExecutionProvider(OrtEnv.Instance(), [target.Value.EpDevice], request.ToProviderOptions());
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Applies WebGPU to an ONNX Runtime GenAI configuration. GenAI has no
    /// device-based append API, so this registers the plugin (making the EP
    /// available to its sessions) and selects it by provider name.
    /// </summary>
    public static bool TryApplyToGenAi(Config config, WebGpuRequest request, out string? error)
    {
        if (!TryRegister(out error))
            return false;

        if (request.DeviceIndex is not null && _devices.Count <= request.DeviceIndex)
        {
            error = $"WebGPU device index {request.DeviceIndex} out of range ({_devices.Count} available)";
            return false;
        }

        try
        {
            config.ClearProviders();
            config.AppendProvider("WebGPU");
            foreach (var (key, value) in request.ToProviderOptions())
                config.SetProviderOption("WebGPU", key, value);
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static DiscoveredDevice? Resolve(int? index)
    {
        lock (Gate)
        {
            if (_devices.Count == 0) return null;
            var i = index ?? 0;
            return i >= 0 && i < _devices.Count ? _devices[i] : null;
        }
    }

    private static void EnsureRegistered(string? pluginPath = null)
    {
        lock (Gate)
        {
            if (_registrationAttempted) return;
            _registrationAttempted = true;

            try
            {
                var path = ResolvePluginPath(pluginPath);
                if (path is null)
                {
                    _registrationError = $"WebGPU plugin library {PluginFileName()} not found next to the application";
                    return;
                }

                var env = OrtEnv.Instance();
                env.RegisterExecutionProviderLibrary(RegistrationName, path);

                foreach (var device in env.GetEpDevices())
                {
                    if (!IsWebGpuEpName(device.EpName)) continue;
                    var hardware = device.HardwareDevice;
                    var info = new WebGpuDeviceInfo(
                        _devices.Count,
                        device.EpName,
                        string.IsNullOrEmpty(hardware.Vendor) ? device.EpVendor : hardware.Vendor,
                        hardware.VendorId,
                        hardware.DeviceId);
                    _devices.Add(new DiscoveredDevice(info, device));
                }

                if (_devices.Count == 0)
                    _registrationError = "WebGPU plugin registered but no adapter was discovered (missing or outdated GPU driver?)";
            }
            catch (Exception e)
            {
                _registrationError = e.Message;
            }
        }
    }

    private static bool IsWebGpuEpName(string epName) =>
        epName.Contains("WebGpu", StringComparison.OrdinalIgnoreCase) ||
        epName.Contains("WebGPU", StringComparison.OrdinalIgnoreCase);

    private static string PluginFileName() => OperatingSystem.IsWindows() ? "onnxruntime_providers_webgpu.dll" : "libonnxruntime_providers_webgpu.so";

    /// <summary>
    /// Resolves the plugin library: explicit argument, then environment override,
    /// then <c>runtimes/&lt;rid&gt;/native</c> followed by the application directory.
    /// </summary>
    private static string? ResolvePluginPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        var fromEnv = Environment.GetEnvironmentVariable(PluginPathEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        var fileName = PluginFileName();
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "runtimes", CurrentRid(), "native", fileName),
            Path.Combine(baseDir, fileName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    private readonly record struct DiscoveredDevice(WebGpuDeviceInfo Info, OrtEpDevice EpDevice);
}

/// <summary>An adapter the WebGPU plugin advertises, as shown to the user.</summary>
/// <param name="Index">Position in the discovered list; used by <c>webgpu:&lt;index&gt;</c>.</param>
/// <param name="EpName">Execution provider name reported by the plugin.</param>
/// <param name="Vendor">Adapter vendor (for example "NVIDIA").</param>
/// <param name="VendorId">PCI vendor id.</param>
/// <param name="DeviceId">PCI device id.</param>
public sealed record WebGpuDeviceInfo(int Index, string EpName, string Vendor, uint VendorId, uint DeviceId);

/// <summary>
/// A parsed <c>webgpu[:selector][,key=value...]</c> execution-provider request.
/// </summary>
/// <remarks>
/// Accepted selectors: <c>hp</c> (high-performance adapter), <c>lp</c> (low-power
/// adapter) or a 0-based device index from <see cref="WebGpuExecutionProvider.GetDevices"/>.
/// Recognized keys: <c>layout</c>, <c>capture</c>, <c>cache</c>, <c>forcecpu</c>,
/// <c>maxssb</c>, <c>power</c>, <c>int64</c>.
/// </remarks>
public sealed record WebGpuRequest
{
    /// <summary>Adapter index from the discovered device list; <see langword="null"/> = first discovered.</summary>
    public int? DeviceIndex { get; init; }

    /// <summary>Dawn adapter hint: "high-performance" or "low-power".</summary>
    public string? PowerPreference { get; init; }

    /// <summary>Preferred data layout: "NCHW" or "NHWC".</summary>
    public string? PreferredLayout { get; init; }

    /// <summary>Enable WebGPU graph capture (needs fully static shapes).</summary>
    public bool? EnableGraphCapture { get; init; }

    /// <summary>Buffer cache mode: disabled, lazyRelease, simple or bucket.</summary>
    public string? BufferCacheMode { get; init; }

    /// <summary>Newline-separated node names forced onto the CPU EP.</summary>
    public string? ForceCpuNodeNames { get; init; }

    /// <summary>Override for the WebGPU device limit on a storage-buffer binding, in bytes.</summary>
    public int? MaxStorageBufferBindingSize { get; init; }

    /// <summary>True when the name denotes a WebGPU request.</summary>
    public static bool IsWebGpuRequest(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var text = name.Trim();
        if (!text.StartsWith("webgpu", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Length == 6 || text[6] is ':' or ',';
    }

    /// <summary>Parses an execution-provider name, or returns <see langword="null"/> when it is not a WebGPU request.</summary>
    public static WebGpuRequest? TryParse(string? name)
    {
        if (!IsWebGpuRequest(name)) return null;

        var request = new WebGpuRequest();
        var text = name!.Trim();
        var body = text.Length > 6 ? text[6..] : "";

        foreach (var rawToken in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = rawToken;
            if (token.StartsWith(':')) token = token[1..];
            if (token.Length == 0) continue;

            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                request = request with { DeviceIndex = ParseDeviceSelector(token) ?? request.DeviceIndex, PowerPreference = ParsePowerPreference(token) ?? request.PowerPreference };
                continue;
            }

            var key = token[..eq].Trim().ToLowerInvariant();
            var value = token[(eq + 1)..].Trim();
            request = key switch
            {
                "power" => request with { PowerPreference = value },
                "layout" => request with { PreferredLayout = value },
                "capture" => request with { EnableGraphCapture = IsTruthy(value) },
                "cache" => request with { BufferCacheMode = value },
                "forcecpu" => request with { ForceCpuNodeNames = value },
                "maxssb" => request with { MaxStorageBufferBindingSize = int.TryParse(value, out var bytes) ? bytes : null },
                "int64" => request with { EnableInt64 = IsTruthy(value) },
                _ => request,
            };
        }

        return request;
    }

    /// <summary>Native int64 support in WGSL kernels (forced on when graph capture is enabled).</summary>
    public bool? EnableInt64 { get; init; }

    /// <summary>Provider options passed to the WebGPU EP (short names, no <c>ep.webgpuexecutionprovider.</c> prefix).</summary>
    public IReadOnlyDictionary<string, string> ToProviderOptions()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (PowerPreference is not null) options["powerPreference"] = PowerPreference;
        if (PreferredLayout is not null) options["preferredLayout"] = PreferredLayout;
        if (EnableGraphCapture is not null) options["enableGraphCapture"] = EnableGraphCapture.Value ? "1" : "0";
        if (EnableInt64 is not null) options["enableInt64"] = EnableInt64.Value ? "1" : "0";
        if (ForceCpuNodeNames is not null) options["forceCpuNodeNames"] = ForceCpuNodeNames;
        if (MaxStorageBufferBindingSize is not null) options["maxStorageBufferBindingSize"] = MaxStorageBufferBindingSize.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (BufferCacheMode is not null)
        {
            options["storageBufferCacheMode"] = BufferCacheMode;
            options["uniformBufferCacheMode"] = BufferCacheMode;
            options["defaultBufferCacheMode"] = BufferCacheMode;
        }

        return options;
    }

    private static int? ParseDeviceSelector(string token) =>
        int.TryParse(token, out var index) && index >= 0 ? index : null;

    private static string? ParsePowerPreference(string token) => token.ToLowerInvariant() switch
    {
        "hp" or "high" or "high-performance" or "highperformance" => "high-performance",
        "lp" or "low" or "low-power" or "lowpower" => "low-power",
        _ => null,
    };

    private static bool IsTruthy(string value) =>
        value is "1" or "true" or "yes" or "on" or "TRUE" or "True";
}
