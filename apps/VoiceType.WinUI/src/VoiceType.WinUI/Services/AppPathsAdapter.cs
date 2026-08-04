using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>Instance adapter for the static AppPaths class, implementing IAppPaths for DI.</summary>
public sealed class AppPathsAdapter : IAppPaths
{
    public string DataRoot => AppPaths.DataRoot;
    public string ModelsDir => AppPaths.ModelsDir;
    public string SessionsDir => AppPaths.SessionsDir;
    public string SettingsFile => AppPaths.SettingsFile;
    public string ErrorLogFile => AppPaths.ErrorLogFile;
    public string TempDir => AppPaths.TempDir;
    public string EnsureDataRoot() => AppPaths.EnsureDataRoot();
    public string EnsureModelsDir() => AppPaths.EnsureModelsDir();
    public string EnsureSessionsDir() => AppPaths.EnsureSessionsDir();
    public string EnsureTempDir() => AppPaths.EnsureTempDir();
}