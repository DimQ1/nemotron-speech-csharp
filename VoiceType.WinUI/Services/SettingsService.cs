using System.IO;
using System.Text.Json;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Serialization;

namespace VoiceType.WinUI.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly string _filePath;
    private readonly object _saveLock = new();
    private DateTime _lastSave = DateTime.MinValue;
    private const int MinSaveIntervalMs = 300;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        TypeInfoResolver = VoiceTypeJsonContext.Default,
        WriteIndented = true
    };

    public SettingsService() : this(AppPaths.SettingsFile) { }
    public SettingsService(string filePath) => _filePath = filePath;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var settings = JsonSerializer.Deserialize(json, VoiceTypeJsonContext.Default.AppSettings);
                if (settings is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Settings] Loaded OK: ModelPath={settings.ModelPath}");
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load FAILED: {ex.Message}");
            try { App.Telemetry?.LogError("Settings", $"Load failed: {ex.Message}"); } catch { }
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_saveLock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSave).TotalMilliseconds < MinSaveIntervalMs)
                return;

            try
            {
                var dir = Path.GetDirectoryName(_filePath)!;
                Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(settings, VoiceTypeJsonContext.Default.AppSettings);
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);

                _lastSave = now;
                System.Diagnostics.Debug.WriteLine($"[Settings] Saved OK: {json.Length} bytes");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Save FAILED: {ex.Message}");
                try { App.Telemetry?.LogError("Settings", $"Save failed: {ex.Message}"); } catch { }
            }
        }
    }
}