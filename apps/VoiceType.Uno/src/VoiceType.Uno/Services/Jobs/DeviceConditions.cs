using System.Runtime.InteropServices;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>
/// Power state for the job scheduler's <c>When charging</c> condition (design §9.5).
/// A device without a battery counts as charging. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public interface IDeviceConditions : IDisposable
{
    /// <summary>Runs on a battery (phone, laptop): <c>When charging</c> can hold jobs back.</summary>
    bool HasBattery { get; }

    /// <summary>On external power, or no battery at all.</summary>
    bool IsCharging { get; }

    event Action? Changed;
}

/// <summary>Picks the power-state source of the running platform.</summary>
public static class DeviceConditions
{
    public static IDeviceConditions Create()
    {
#if __ANDROID__
        return new VoiceType.Uno.Droid.AndroidDeviceConditions();
#else
        if (OperatingSystem.IsWindows())
            return new PollingDeviceConditions(WindowsPowerStatus.Read);
        if (OperatingSystem.IsLinux())
            return new PollingDeviceConditions(LinuxPowerSupply.Read);
        return new PollingDeviceConditions(static () => (false, true));
#endif
    }
}

/// <summary>Reads the power state now and every <see cref="Interval"/>; raises <see cref="Changed"/> on a difference.</summary>
public sealed class PollingDeviceConditions : IDeviceConditions
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    private readonly Func<(bool HasBattery, bool IsCharging)> _read;
    private readonly Timer _timer;
    private (bool HasBattery, bool IsCharging) _state;

    public PollingDeviceConditions(Func<(bool HasBattery, bool IsCharging)> read)
    {
        _read = read;
        _state = SafeRead();
        _timer = new Timer(_ => Poll(), null, Interval, Interval);
    }

    public bool HasBattery => _state.HasBattery;
    public bool IsCharging => _state.IsCharging;

    public event Action? Changed;

    private void Poll()
    {
        var next = SafeRead();
        if (next == _state)
            return;
        _state = next;
        Changed?.Invoke();
    }

    private (bool, bool) SafeRead()
    {
        try
        {
            return _read();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Power state unavailable: {ex.Message}");
            return (false, true);
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>Windows: <c>GetSystemPowerStatus</c> (AC line + battery flag).</summary>
internal static class WindowsPowerStatus
{
    private const byte AcOnline = 1;
    private const byte NoSystemBattery = 128;
    private const byte UnknownStatus = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static (bool HasBattery, bool IsCharging) Read()
    {
        if (!GetSystemPowerStatus(out var status))
            return (false, true);
        var hasBattery = status.BatteryFlag is not NoSystemBattery and not UnknownStatus;
        return (hasBattery, !hasBattery || status.ACLineStatus == AcOnline);
    }
}

/// <summary>Linux: <c>/sys/class/power_supply/*</c> (<c>type</c>, <c>online</c>, <c>present</c>, <c>status</c>).</summary>
internal static class LinuxPowerSupply
{
    private const string Root = "/sys/class/power_supply";

    public static (bool HasBattery, bool IsCharging) Read()
    {
        if (!Directory.Exists(Root))
            return (false, true);

        bool hasBattery = false, external = false, batteryCharging = false;
        foreach (var supply in Directory.EnumerateDirectories(Root))
        {
            var type = ReadValue(supply, "type");
            if (string.Equals(type, "Battery", StringComparison.OrdinalIgnoreCase))
            {
                // Peripheral batteries (mouse, headset) report scope=Device.
                if (string.Equals(ReadValue(supply, "scope"), "Device", StringComparison.OrdinalIgnoreCase)
                    || ReadValue(supply, "present") == "0")
                    continue;
                hasBattery = true;
                var status = ReadValue(supply, "status");
                if (status is "Charging" or "Full" or "Not charging")
                    batteryCharging = true;
            }
            else if (ReadValue(supply, "online") == "1")
            {
                external = true; // Mains, USB, USB-C
            }
        }
        return (hasBattery, !hasBattery || external || batteryCharging);
    }

    private static string? ReadValue(string supply, string name)
    {
        try
        {
            var path = Path.Combine(supply, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
