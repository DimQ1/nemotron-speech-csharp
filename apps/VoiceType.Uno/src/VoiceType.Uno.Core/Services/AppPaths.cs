namespace VoiceType.Uno.Services;

/// <summary>
/// Single source of truth for application data locations.
/// Cross-platform: uses %LOCALAPPDATA% on Windows, $XDG_DATA_HOME (~/.local/share) on Linux,
/// ~/Library/Application Support on macOS.
/// </summary>
public static class AppPaths
{
    private static string? s_dataRoot;

    public static string DataRoot => s_dataRoot ??= EnsureDataRoot();

    public static string ModelsDir => Path.Combine(DataRoot, "models");
    public static string SessionsDir => Path.Combine(DataRoot, "sessions");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string ErrorLogFile => Path.Combine(DataRoot, "errors.log");
    public static string TempDir => Path.Combine(DataRoot, "temp");

    public static string EnsureDataRoot()
    {
        var root = ResolveDataRoot();
        Directory.CreateDirectory(root);
        return root;
    }

    public static string EnsureModelsDir() { var d = ModelsDir; Directory.CreateDirectory(d); return d; }
    public static string EnsureSessionsDir() { var d = SessionsDir; Directory.CreateDirectory(d); return d; }
    public static string EnsureTempDir() { var d = TempDir; Directory.CreateDirectory(d); return d; }

    private static string ResolveDataRoot()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        return Path.Combine(baseDir, "VoiceType");
    }

    /// <summary>Reset the cached root — only for tests.</summary>
    internal static void ResetForTests() => s_dataRoot = null;
}
