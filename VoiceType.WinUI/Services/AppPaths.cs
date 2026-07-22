using System.IO;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Centralized data paths. All app data lives under <c>%LOCALAPPDATA%\VoiceType</c>.
/// </summary>
public static class AppPaths
{
    private static string? _dataRoot;

    /// <summary>Root data folder: <c>%LOCALAPPDATA%\VoiceType</c>.</summary>
    public static string DataRoot
    {
        get
        {
            if (_dataRoot is not null) return _dataRoot;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _dataRoot = Path.Combine(localAppData, "VoiceType");
            return _dataRoot;
        }
    }

    /// <summary>Downloaded models: <c>data/Models</c>.</summary>
    public static string ModelsDir => Path.Combine(DataRoot, "Models");

    /// <summary>Recognition sessions: <c>data/Sessions</c>.</summary>
    public static string SessionsDir => Path.Combine(DataRoot, "Sessions");

    /// <summary>Settings file: <c>data/settings.json</c>.</summary>
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>Error log file: <c>data/error.log</c>.</summary>
    public static string ErrorLogFile => Path.Combine(DataRoot, "error.log");

    /// <summary>Temporary files (e.g. in-progress MP3 encoding): <c>data/temp</c>.</summary>
    public static string TempDir => Path.Combine(DataRoot, "temp");

    /// <summary>Create the data root (and all known subfolders) if missing.</summary>
    public static string EnsureDataRoot()
    {
        Directory.CreateDirectory(DataRoot);
        return DataRoot;
    }

    public static string EnsureModelsDir()
    {
        Directory.CreateDirectory(ModelsDir);
        return ModelsDir;
    }

    public static string EnsureSessionsDir()
    {
        Directory.CreateDirectory(SessionsDir);
        return SessionsDir;
    }

    public static string EnsureTempDir()
    {
        Directory.CreateDirectory(TempDir);
        return TempDir;
    }
}
