using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Structured error logging and telemetry service.
/// Writes to file, debug output, and optionally exports to Aspire Dashboard via OTLP.
/// </summary>
public sealed class ErrorTelemetryService : ISystemTelemetry, IDisposable
{
    private readonly ILogger<ErrorTelemetryService> _logger;
    private readonly string _logFile;
    private readonly object _sync = new();

    public ErrorTelemetryService(ILogger<ErrorTelemetryService> logger)
    {
        _logger = logger;
        AppPaths.EnsureDataRoot();
        _logFile = AppPaths.ErrorLogFile;
    }

    public void LogError(string category, string message, Exception? ex = null)
    {
        var line = FormatLine("ERROR", category, message, ex);
        _logger.LogError(ex, "[{Category}] {Message}", category, message);
        WriteToFile(line);
        Debug.WriteLine(line);
    }

    public void LogWarning(string category, string message)
    {
        var line = FormatLine("WARN", category, message);
        _logger.LogWarning("[{Category}] {Message}", category, message);
        WriteToFile(line);
        Debug.WriteLine(line);
    }

    public void LogInfo(string category, string message)
    {
        var line = FormatLine("INFO", category, message);
        _logger.LogInformation("[{Category}] {Message}", category, message);
        WriteToFile(line);
    }

    public void TrackEvent(string name, IDictionary<string, object?>? properties = null)
    {
        var props = properties is not null
            ? string.Join(", ", properties.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        _logger.LogInformation("[EVENT] {Name} | {Props}", name, props);
        Debug.WriteLine($"[TELEMETRY] Event: {name} | {props}");
    }

    public void TrackMetric(string name, double value, IDictionary<string, object?>? properties = null)
    {
        var props = properties is not null
            ? string.Join(", ", properties.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        _logger.LogInformation("[METRIC] {Name} = {Value} | {Props}", name, value, props);
    }

    private static string FormatLine(string level, string category, string message, Exception? ex = null)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var exInfo = ex is not null ? $" | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : "";
        return $"[{timestamp}] [{level}] [{category}] {message}{exInfo}";
    }

    private void WriteToFile(string line)
    {
        lock (_sync)
        {
            try
            {
                // Atomic append shared across processes; avoids StreamWriter lock contention.
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        // No-op: file is opened per-write.
    }
}