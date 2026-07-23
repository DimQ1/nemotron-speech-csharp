using System.IO;
using System.Text.Json;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

public sealed class SessionManager : ISessionManager
{
    private static string SessionsDir => AppPaths.SessionsDir;

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
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
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
                var s = JsonSerializer.Deserialize<RecognitionSession>(File.ReadAllText(f));
                if (s is not null) sessions.Add(s);
            }
            catch { }
        }
        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }
}