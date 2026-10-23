using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// What the machine offers for the real WebGPU tests, found out in a child process (<c>WerEval --list-gpus</c>), never in the test
/// host: registering the plugin and asking the driver is the part that can take a process down.
/// </summary>
internal static class WebGpuHardware
{
    private static readonly Lazy<string?> Reason = new(FindSkipReason);

    /// <summary>Null when the real WebGPU tests can run (WerEval built, model and audio found, plugin loaded, a discrete card present); otherwise why not.</summary>
    public static string? SkipReason => Reason.Value;

    /// <summary>The built <c>WerEval</c> of the repository in the configuration of the test binaries, or null.</summary>
    public static string? WerEvalPath()
    {
        var root = NemotronTestEnvironment.FindRepositoryRoot();
        var framework = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var configuration = framework.Parent?.Name;
        if (root is null || configuration is null)
            return null;
        var path = Path.Combine(root, "tools", "WerEval", "bin", configuration, framework.Name, "WerEval.dll");
        return File.Exists(path) ? path : null;
    }

    private static string? FindSkipReason()
    {
        var werEval = WerEvalPath();
        if (werEval is null)
            return "tools/WerEval is not built in this configuration (build NemotronSpeech.slnx).";
        if (NemotronTestEnvironment.LocateModel("int4-c056") is null)
            return "Nemotron model folder 'int4-c056' not found.";
        if (NemotronTestEnvironment.FindAudio("cv17/en/0001.wav") is null)
            return "Test-Audio/cv17 not found (set SPEECHLIB_TEST_AUDIO).";

        var probe = ChildProcess.Run("dotnet", [werEval, "--list-gpus"], TimeSpan.FromSeconds(90));
        if (!probe.Succeeded)
        {
            var line = probe.Output.Split('\n').FirstOrDefault(l => l.Contains("unavailable", StringComparison.OrdinalIgnoreCase))?.Trim();
            return $"WebGPU is not usable here ({(line is not null ? line : probe.Describe())}).";
        }
        if (!probe.Output.Contains(", discrete)", StringComparison.Ordinal))
            return $"no discrete graphics card (adapters: {string.Join("; ", probe.Output.Split('\n').Where(l => l.Contains("vendor 0x")).Select(l => l.Trim()))}); Nemotron uses WebGPU on a discrete card only.";
        return null;
    }

    /// <summary>Physical memory that is free right now, or null when this platform does not tell.</summary>
    public static long? FreeMemoryBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : null;
            }
            if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
                var kilobytes = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                return kilobytes is not null ? long.Parse(kilobytes, CultureInfo.InvariantCulture) * 1024 : null;
            }
        }
        catch (Exception e) when (e is IOException or FormatException or IndexOutOfRangeException)
        {
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>Runs the test only on a machine with the WebGPU plugin and a discrete graphics card (see <see cref="WebGpuHardware"/>); the skip says what is missing.</summary>
public sealed class WebGpuDiscreteFactAttribute : FactAttribute
{
    public WebGpuDiscreteFactAttribute()
    {
        Skip = WebGpuHardware.SkipReason;
    }
}
