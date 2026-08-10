using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Uno.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class VoiceTypeJsonContext : JsonSerializerContext;

/// <summary>
/// JSON-file backed settings persistence (atomic write via temp file + replace).
/// </summary>
public sealed class SettingsService
{
    private readonly string _filePath;
    private readonly object _saveLock = new();

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
                    return settings;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load FAILED: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_saveLock)
            SaveCore(settings);
    }

    public void Update(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_saveLock)
        {
            var settings = Load();
            update(settings);
            SaveCore(settings);
        }
    }

    private void SaveCore(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(settings, VoiceTypeJsonContext.Default.AppSettings);
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Save FAILED: {ex.Message}");
        }
    }
}
