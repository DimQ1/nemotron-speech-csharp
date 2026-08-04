namespace VoiceType.WinUI.Interfaces;

/// <summary>System-level telemetry: error tracking, events, performance.</summary>
public interface ISystemTelemetry
{
    void LogError(string category, string message, Exception? ex = null);
    void LogWarning(string category, string message);
    void LogInfo(string category, string message);
    void TrackEvent(string name, IDictionary<string, object?>? properties = null);
    void TrackMetric(string name, double value, IDictionary<string, object?>? properties = null);
}