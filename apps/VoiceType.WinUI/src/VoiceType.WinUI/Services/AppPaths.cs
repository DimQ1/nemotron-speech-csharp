using System.IO;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Centralized data paths. All app data lives under a per-user VoiceType folder.
/// When running packaged (MSIX), <c>%LOCALAPPDATA%</c> is virtualized by Windows to the
/// package's private <c>LocalCache\Local</c>, so we return the REAL on-disk path
/// (<c>...Packages\&lt;family&gt;\LocalCache\Local\VoiceType</c>) that the user can actually
/// open in Explorer. When unpackaged (dotnet run), it is simply <c>%LOCALAPPDATA%\VoiceType</c>.
/// </summary>
public static class AppPaths
{
    private static string? _dataRoot;

    /// <summary>True when the app runs as an installed MSIX package.</summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current is not null;
            }
            catch
            {
                return false; // unpackaged — Package.Current throws
            }
        }
    }

    /// <summary>Root data folder — the REAL path on disk (Explorer-visible).</summary>
    public static string DataRoot
    {
        get
        {
            if (_dataRoot is not null) return _dataRoot;

            if (IsPackaged)
            {
                // For packaged apps, GetFolderPath(LocalApplicationData) returns the VIRTUAL
                // path (%LOCALAPPDATA%), but Windows actually redirects writes to the package's
                // private %LOCALAPPDATA%\Packages\<family>\LocalCache\Local. Return the real,
                // Explorer-visible location so paths shown in Settings are browsable.
                var familyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                var baseLocal = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _dataRoot = Path.Combine(baseLocal, "Packages", familyName, "LocalCache", "Local", "VoiceType");
                return _dataRoot;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _dataRoot = Path.Combine(localAppData, "VoiceType");
            return _dataRoot;
        }
    }

    /// <summary>Downloaded models: <c>data/Models</c>.</summary>
    public static string ModelsDir => Path.Combine(DataRoot, "Models");

    /// <summary>Translation models (LiteRT <c>.litertlm</c>): <c>data/Models/Translation</c>.</summary>
    public static string TranslationModelsDir => Path.Combine(ModelsDir, "Translation");

    /// <summary>Recognition sessions: <c>data/Sessions</c>.</summary>
    public static string SessionsDir => Path.Combine(DataRoot, "Sessions");

    /// <summary>Settings file: <c>data/settings.json</c>.</summary>
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>Error log file: <c>data/error.log</c>.</summary>
    public static string ErrorLogFile => Path.Combine(DataRoot, "error.log");

    /// <summary>Temporary files (e.g. in-progress MP3 encoding): <c>data/temp</c>.</summary>
    public static string TempDir => Path.Combine(DataRoot, "temp");

    /// <summary>
    /// Bundled Silero VAD model (app content shipped in the package, not user data).
    /// Used to gate speech recognition on all models without built-in VAD.
    /// </summary>
    public static string SileroVadPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");

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

    public static string EnsureTranslationModelsDir()
    {
        Directory.CreateDirectory(TranslationModelsDir);
        return TranslationModelsDir;
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
