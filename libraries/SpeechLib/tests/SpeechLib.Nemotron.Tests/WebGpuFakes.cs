using Microsoft.ML.OnnxRuntime;
using SpeechLib.Nemotron;

namespace SpeechLib.Nemotron.Tests;

/// <summary>Adapters without a GPU: a list the test fills, and a record of what the policy asked for.</summary>
internal sealed class FakeAdapters(List<string> events) : IWebGpuAdapters
{
    public static readonly WebGpuDeviceInfo Integrated =
        new(0, "WebGpuExecutionProvider", "Intel Corporation", 0x8086, 0x7D67, IsDiscrete: false, Description: "Intel(R) Graphics");

    public static readonly WebGpuDeviceInfo Discrete =
        new(1, "WebGpuExecutionProvider", "NVIDIA", 0x10DE, 0x2F18, IsDiscrete: true, Description: "NVIDIA GeForce RTX 5070 Ti Laptop GPU");

    public string? UnavailableReason { get; set; }
    public List<WebGpuDeviceInfo> DeviceList { get; set; } = [Integrated, Discrete];
    public int Queries { get; private set; }
    public bool ApplyResult { get; set; } = true;
    public List<WebGpuDeviceInfo> Applied { get; } = [];

    public IReadOnlyList<WebGpuDeviceInfo> Devices
    {
        get
        {
            Queries++;
            return DeviceList;
        }
    }

    public bool TryApplyHighPerformance(SessionOptions options, WebGpuDeviceInfo device, out string? error)
    {
        events.Add("apply");
        Applied.Add(device);
        error = ApplyResult ? null : "the provider could not be appended";
        return ApplyResult;
    }
}

/// <summary>A crash marker in memory that writes its calls into the shared event list.</summary>
internal sealed class FakeMarker(List<string> events) : IWebGpuCrashMarker
{
    public string? Stale { get; set; }
    public int Armed { get; private set; }
    public int Disarmed { get; private set; }
    public int Cleared { get; private set; }

    public string? FindStale() => Stale;

    public void Arm()
    {
        Armed++;
        events.Add("arm");
    }

    public void Disarm()
    {
        Disarmed++;
        events.Add("disarm");
    }

    public void ClearStale()
    {
        Cleared++;
        Stale = null;
        events.Add("clear");
    }
}

/// <summary>
/// Everything the Nemotron provider policy and the session creation touch, replaced: adapters, crash marker, process gate,
/// session factory, log and environment. <see cref="Events"/> is the order in which the parts were used.
/// </summary>
internal sealed class WebGpuRig
{
    public WebGpuRig()
    {
        Adapters = new FakeAdapters(Events);
        Marker = new FakeMarker(Events);
    }

    public List<string> Events { get; } = [];
    public List<string> Log { get; } = [];
    public FakeAdapters Adapters { get; }
    public FakeMarker Marker { get; }
    public WebGpuProcessGate Gate { get; } = new();
    public Dictionary<string, string?> Environment { get; } = new();

    /// <summary>Graph file name and whether the options carried WebGPU, for every session created.</summary>
    public List<(string Graph, bool WebGpu)> Created { get; } = [];

    /// <summary>Makes the creation of a session throw; null creates it.</summary>
    public Func<string, bool, Exception?>? Fail { get; set; }

    public NemotronProviderServices Services => new()
    {
        Adapters = Adapters,
        Marker = Marker,
        Gate = Gate,
        Log = Log.Add,
        GetEnvironmentVariable = name => Environment.GetValueOrDefault(name),
        CreateSession = (path, options, webGpu) =>
        {
            var graph = Path.GetFileName(path);
            Events.Add(webGpu ? $"create {graph} webgpu" : $"create {graph}");
            if (Fail?.Invoke(graph, webGpu) is { } failure)
                throw failure;
            Created.Add((graph, webGpu));
            return new InferenceSession(path, options);
        },
    };
}

/// <summary>A valid ONNX model that does nothing (Identity on float[1]), written by hand so that no model file is needed.</summary>
internal static class TinyOnnxModel
{
    /// <summary>The bytes of the model: input <c>x</c>, output <c>y</c>.</summary>
    public static byte[] Identity()
    {
        var dimension = Varint(1, 1);                                              // Dimension.dim_value = 1
        var shape = Bytes(1, dimension);                                           // TensorShapeProto.dim
        var tensor = Concat(Varint(1, 1), Bytes(2, shape));                        // Tensor.elem_type = FLOAT, shape
        var type = Bytes(1, tensor);                                               // TypeProto.tensor_type
        byte[] ValueInfo(string name) => Concat(Bytes(1, Text(name)), Bytes(2, type));
        var node = Concat(Bytes(1, Text("x")), Bytes(2, Text("y")), Bytes(4, Text("Identity")));
        var graph = Concat(Bytes(1, node), Bytes(2, Text("identity")), Bytes(11, ValueInfo("x")), Bytes(12, ValueInfo("y")));
        var opset = Varint(2, 13);                                                 // OperatorSetIdProto.version
        return Concat(Varint(1, 8), Bytes(8, opset), Bytes(7, graph));             // ir_version, opset_import, graph
    }

    /// <summary>A folder with the three graph files of <paramref name="config"/> (each the identity model); the caller deletes it.</summary>
    public static string CreateModelFolder(NemotronModelConfig config)
    {
        var folder = Path.Combine(Path.GetTempPath(), "nemotron-tiny-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var file in new[] { config.Encoder.FileName, config.Decoder.FileName, config.Joiner.FileName })
            File.WriteAllBytes(Path.Combine(folder, file), Identity());
        return folder;
    }

    private static byte[] Text(string value) => System.Text.Encoding.ASCII.GetBytes(value);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Varint(int field, int value) => Concat(Raw((ulong)(field << 3)), Raw((ulong)value));

    private static byte[] Bytes(int field, byte[] payload) => Concat(Raw((ulong)((field << 3) | 2)), Raw((ulong)payload.Length), payload);

    private static byte[] Raw(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? next : (byte)(next | 0x80));
        }
        while (value != 0);
        return bytes.ToArray();
    }
}
