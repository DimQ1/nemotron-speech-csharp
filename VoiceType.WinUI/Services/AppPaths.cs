using System.IO;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Centralized data paths. All app data lives under <c>data/</c> next to the
/// application executable, so the app is portable and works on first run.
/// </summary>
public static class AppPaths
{
    /// <summary>Root data folder: <c>&lt;app dir&gt;/data</c>.</summary>
    public static string DataRoot { get; } =
        Path.Combine(AppContext.BaseDirectory, "data");

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
