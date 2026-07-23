namespace VoiceType.WinUI.Interfaces;

public interface IAppPaths
{
    string DataRoot { get; }
    string ModelsDir { get; }
    string SessionsDir { get; }
    string SettingsFile { get; }
    string ErrorLogFile { get; }
    string TempDir { get; }
    string EnsureDataRoot();
    string EnsureModelsDir();
    string EnsureSessionsDir();
    string EnsureTempDir();
}
