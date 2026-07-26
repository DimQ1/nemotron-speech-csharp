using System.IO;
using System.Text.Json;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Serialization;

namespace VoiceType.WinUI.Services;

public sealed class SettingsService : ISettingsService
{
    private readonly string _filePath;

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
                return JsonSerializer.Deserialize(json, VoiceTypeJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(settings, VoiceTypeJsonContext.Default.AppSettings);
        File.WriteAllText(_filePath, json);
    }
}