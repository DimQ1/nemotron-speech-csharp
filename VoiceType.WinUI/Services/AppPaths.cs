using System.IO;
using Windows.Storage;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Centralized data paths. For MSIX packaged apps, data lives in
/// <c>%LOCALAPPDATA%\Packages\&lt;pkg&gt;\LocalState\data</c>.
/// Falls back to <c>&lt;app dir&gt;/data</c> for unpackaged.
/// </summary>
public static class AppPaths
{
    private static string? _dataRoot;

    /// <summary>Root data folder. MSIX: LocalState/data. Unpackaged: appdir/data.</summary>
    public static string DataRoot
    {
        get
        {
            if (_dataRoot is not null) return _dataRoot;

            try
            {
                // MSIX packaged — use LocalState
                var localState = ApplicationData.Current.LocalFolder.Path;
                _dataRoot = Path.Combine(localState, "data");
            }
            catch
            {
                // Unpackaged — next to exe
                _dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
            }
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
