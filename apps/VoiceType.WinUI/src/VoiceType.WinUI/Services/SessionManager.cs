using System.IO;
using System.Text.Json;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Serialization;

namespace VoiceType.WinUI.Services;

public sealed class SessionManager : ISessionManager
{
    private readonly ISettingsService? _settingsService;

    public SessionManager(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    private string SessionsDir
    {
        get
        {
            var configuredPath = _settingsService?.Load().SessionsPath;
            if (string.IsNullOrWhiteSpace(configuredPath))
                return AppPaths.SessionsDir;

            try
            {
                return Path.GetFullPath(configuredPath);
            }
            catch (ArgumentException)
            {
                return AppPaths.SessionsDir;
            }
        }
    }

    public string EnsureDirectory()
    {
        Directory.CreateDirectory(SessionsDir);
        return SessionsDir;
    }

    public RecognitionSession CreateSession(string language, string engine, string audioSource)
    {
        return new RecognitionSession
        {
            Language = language,
            EngineProvider = engine,
            AudioSource = audioSource
        };
    }

    public void SaveSession(RecognitionSession session)
    {
        var dir = EnsureDirectory();
        var jsonPath = Path.Combine(dir, string.Concat(session.FileNameBase, ".json"));
        var json = JsonSerializer.Serialize(session, VoiceTypeJsonContext.Default.RecognitionSession);
        File.WriteAllText(jsonPath, json);
    }

    public List<RecognitionSession> LoadSessions()
    {
        var dir = EnsureDirectory();
        var sessions = new List<RecognitionSession>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize(File.ReadAllText(f), VoiceTypeJsonContext.Default.RecognitionSession);
                if (s is not null) sessions.Add(s);
            }
            catch { }
        }
        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }
}