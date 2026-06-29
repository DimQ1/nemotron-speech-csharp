using System.IO;
using System.Text.Json;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>Manages speech recognition sessions: creation, persistence, listing.</summary>
public static class SessionManager
{
    private static string SessionsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType", "Sessions");

    public static string EnsureDirectory()
    {
        Directory.CreateDirectory(SessionsDir);
        return SessionsDir;
    }

    public static RecognitionSession CreateSession(string language, string engine, string audioSource)
    {
        return new RecognitionSession
        {
            Language = language,
            EngineProvider = engine,
            AudioSource = audioSource
        };
    }

    public static void SaveSession(RecognitionSession session)
    {
        var dir = EnsureDirectory();
        var jsonPath = Path.Combine(dir, $"{session.Id}.json");
        var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
    }

    public static List<RecognitionSession> LoadSessions()
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
            catch { /* skip corrupted */ }
        }
        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }
}
