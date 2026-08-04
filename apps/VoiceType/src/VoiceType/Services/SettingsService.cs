using System.IO;
using System.Text.Json;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public static class SettingsService
{
    private static readonly string FilePath = AppPaths.SettingsFile;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* corrupted file → use defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
