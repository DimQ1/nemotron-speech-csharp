using System.Diagnostics;
using System.Globalization;

namespace SpeechLib.Nemotron;

/// <summary>
/// Remembers that a process was using the WebGPU execution provider, so that the next start can tell that the process died
/// there: a native crash of the plugin or the GPU driver cannot be caught, but it leaves the marker behind.
/// </summary>
/// <remarks>Every member tolerates a store that cannot be read or written: a failing marker must never stop a recognizer.</remarks>
internal interface IWebGpuCrashMarker
{
    /// <summary>Describes a marker whose process is gone, or returns null when there is none.</summary>
    string? FindStale();

    /// <summary>Records that this process is about to create and use a WebGPU session.</summary>
    void Arm();

    /// <summary>Removes this process's record: WebGPU has proven itself, or the session was closed cleanly. Does nothing when not armed.</summary>
    void Disarm();

    /// <summary>Deletes the markers of processes that are gone: WebGPU may be tried again.</summary>
    void ClearStale();
}

/// <summary>
/// <see cref="IWebGpuCrashMarker"/> in files: one <c>active-&lt;pid&gt;.marker</c> per process that armed it, in
/// <c>%LOCALAPPDATA%\SpeechLib\webgpu</c> (<c>~/.local/share/SpeechLib/webgpu</c> on Linux) or the folder named by the
/// <c>SPEECHLIB_WEBGPU_STATE_DIR</c> environment variable.
/// </summary>
/// <remarks>
/// A file is stale when its process no longer exists (the start time in the file guards against a reused process id); the
/// file of a live process, also of another one using the GPU at the same time, is not. A stale marker is not removed by
/// <see cref="FindStale"/>: it keeps WebGPU off for every start until <see cref="ClearStale"/> runs (the policy does that when
/// <c>SPEECHLIB_WEBGPU_RETRY=1</c> is set), so a crash cannot repeat. Two people never share a marker: it lives in the
/// user's own application-data folder.
/// </remarks>
internal sealed class WebGpuCrashMarker : IWebGpuCrashMarker
{
    /// <summary>Environment variable that moves the marker folder (tests, sandboxes).</summary>
    internal const string DirectoryVariable = "SPEECHLIB_WEBGPU_STATE_DIR";

    private const string FilePrefix = "active-";
    private const string FileSuffix = ".marker";
    private const string StartedKey = "started=";

    private static readonly Lazy<WebGpuCrashMarker> DefaultInstance = new(() => new WebGpuCrashMarker(DefaultDirectory()));

    private readonly string _directory;
    private readonly Func<int, long, bool> _isAlive;
    private readonly int _processId = Environment.ProcessId;
    private long _startTicks;
    private readonly object _sync = new();
    private bool _armed;

    /// <summary>Markers in <paramref name="directory"/>.</summary>
    /// <param name="directory">The folder; created when the marker is first armed.</param>
    /// <param name="isAlive">Tells whether a process (id, start time in UTC ticks, 0 when unknown) still exists; null looks at the operating system.</param>
    public WebGpuCrashMarker(string directory, Func<int, long, bool>? isAlive = null)
    {
        _directory = directory;
        _isAlive = isAlive ?? ProcessIsAlive;
    }

    /// <summary>The marker every recognizer of the process uses.</summary>
    public static WebGpuCrashMarker Default => DefaultInstance.Value;

    /// <summary>The default folder: the environment override, else the application-data folder of the user.</summary>
    /// <param name="getEnvironmentVariable">Reads an environment variable; null reads the real one.</param>
    internal static string DefaultDirectory(Func<string, string?>? getEnvironmentVariable = null)
    {
        var overridden = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
            local = Path.GetTempPath();
        return Path.Combine(local, "SpeechLib", "webgpu");
    }

    /// <inheritdoc />
    public string? FindStale() => StaleFiles().FirstOrDefault().Description;

    /// <inheritdoc />
    public void Arm()
    {
        lock (_sync)
        {
            try
            {
                if (_startTicks == 0)
                    _startTicks = ProcessStartTicks(_processId);
                Directory.CreateDirectory(_directory);
                File.WriteAllText(PathFor(_processId),
                    "SpeechLib WebGPU marker: a process that dies while this file exists is assumed to have crashed in the WebGPU execution provider.\n" +
                    "Delete the file, or start once with SPEECHLIB_WEBGPU_RETRY=1, to try WebGPU again.\n" +
                    $"process={_processId.ToString(CultureInfo.InvariantCulture)}\n" +
                    $"{StartedKey}{_startTicks.ToString(CultureInfo.InvariantCulture)}\n" +
                    $"written={DateTime.UtcNow:O}\n");
                _armed = true;
            }
            catch (Exception e) when (IsStoreFailure(e))
            {
            }
        }
    }

    /// <inheritdoc />
    public void Disarm()
    {
        lock (_sync)
        {
            if (!_armed)
                return;
            _armed = false;
            TryDelete(PathFor(_processId));
        }
    }

    /// <inheritdoc />
    public void ClearStale()
    {
        foreach (var (path, _) in StaleFiles())
            TryDelete(path);
    }

    private string PathFor(int processId) => Path.Combine(_directory, $"{FilePrefix}{processId.ToString(CultureInfo.InvariantCulture)}{FileSuffix}");

    private IEnumerable<(string Path, string Description)> StaleFiles()
    {
        string[] files;
        try
        {
            if (!Directory.Exists(_directory))
                yield break;
            files = Directory.GetFiles(_directory, FilePrefix + "*" + FileSuffix);
        }
        catch (Exception e) when (IsStoreFailure(e))
        {
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!int.TryParse(name.AsSpan(FilePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
                continue;

            var started = ReadStartTicks(file);
            if (!_isAlive(processId, started))
                yield return (file, $"process {processId} ended while it was using WebGPU (marker {file})");
        }
    }

    private static long ReadStartTicks(string file)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
                if (line.StartsWith(StartedKey, StringComparison.Ordinal) && long.TryParse(line.AsSpan(StartedKey.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                    return ticks;
        }
        catch (Exception e) when (IsStoreFailure(e))
        {
        }
        return 0;   // an unreadable or half-written file: judge by the process id alone
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (IsStoreFailure(e))
        {
        }
    }

    private static bool IsStoreFailure(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException;

    private static long ProcessStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool ProcessIsAlive(int processId, long startTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return false;
            // The same id with another start time is a different process that reused the number.
            return startTicks == 0 || Math.Abs(process.StartTime.ToUniversalTime().Ticks - startTicks) < 2 * TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException)
        {
            return false;   // no such process
        }
        catch (Exception)
        {
            return true;    // cannot tell (access denied): do not accuse a process that may be running
        }
    }
}
