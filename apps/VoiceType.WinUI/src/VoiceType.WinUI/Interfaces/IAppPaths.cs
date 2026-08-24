namespace VoiceType.WinUI.Interfaces;

public interface IAppPaths
{
    string DataRoot { get; }
    string ModelsDir { get; }
    string TranslationModelsDir { get; }
    string SessionsDir { get; }
    string SettingsFile { get; }
    string ErrorLogFile { get; }
    string TempDir { get; }
    string EnsureDataRoot();
    string EnsureModelsDir();
    string EnsureTranslationModelsDir();
    string EnsureSessionsDir();
    string EnsureTempDir();
}
