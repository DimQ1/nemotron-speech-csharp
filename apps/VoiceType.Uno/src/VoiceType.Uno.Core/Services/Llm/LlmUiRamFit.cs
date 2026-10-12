namespace VoiceType.Uno.Services;

/// <summary>Reads the memory that is free on this device right now.</summary>
public interface IDeviceMemoryProvider
{
    /// <summary>Free physical memory in bytes, or null when it cannot be read.</summary>
    long? GetAvailableBytes();
}

/// <summary>
/// Free memory from the runtime's own view of the machine: the memory the GC may use
/// (the physical RAM, or the container / Android limit) minus the current load.
/// Works the same on Windows, Linux and Android.
/// </summary>
public sealed class GcDeviceMemoryProvider : IDeviceMemoryProvider
{
    public long? GetAvailableBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes <= 0)
                return null;

            return Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Free RAM of this device in GB (injectable for tests).</summary>
public static class DeviceMemory
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    /// <summary>Replaced in tests.</summary>
    public static IDeviceMemoryProvider Provider { get; set; } = new GcDeviceMemoryProvider();

    /// <summary>Free RAM in GB, or null when unknown.</summary>
    public static double? AvailableGb => ToGb(Provider.GetAvailableBytes());

    public static double? ToGb(long? bytes) => bytes is { } b and >= 0 ? b / BytesPerGb : null;
}

public enum LlmUiRamFit { Unknown, Fits, Low }

/// <summary>Compares a model's memory need with the free memory of the device.</summary>
public static class LlmUiRamCheck
{
    public static LlmUiRamFit Evaluate(double minFreeRamGb, double? availableGb)
    {
        if (availableGb is null)
            return LlmUiRamFit.Unknown;

        return availableGb.Value < minFreeRamGb ? LlmUiRamFit.Low : LlmUiRamFit.Fits;
    }

    /// <summary>"Needs ≈6 GB free RAM · 9.2 GB free now".</summary>
    public static string Describe(double minFreeRamGb, double? availableGb)
    {
        var need = $"Needs ≈{minFreeRamGb:0.#} GB free RAM";
        return availableGb is { } free ? $"{need} · {free:0.#} GB free now" : need;
    }
}
