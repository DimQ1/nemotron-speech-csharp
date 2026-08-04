namespace VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

public interface ISessionManager
{
    string EnsureDirectory();
    RecognitionSession CreateSession(string language, string engine, string audioSource);
    void SaveSession(RecognitionSession session);
    List<RecognitionSession> LoadSessions();
}
