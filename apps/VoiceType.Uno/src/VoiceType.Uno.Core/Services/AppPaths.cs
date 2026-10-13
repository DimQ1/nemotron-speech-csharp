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

    /// <summary>
    /// The default data root (<c>%LOCALAPPDATA%\VoiceType</c>, <c>~/.local/share/VoiceType</c>). Holds the
    /// <see cref="DataLocation.PointerFileName"/> pointer when the data lives elsewhere.
    /// </summary>
    public static string DefaultDataRoot
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(baseDir, "VoiceType");
        }
    }

    /// <summary>
    /// True when the DEBUG-only sandbox (<c>VOICETYPE_DATA_ROOT</c>) names the data root: it beats the pointer file, and
    /// "Move app data" is not offered because the sandbox folder is throwaway.
    /// </summary>
    public static bool IsDataRootSandboxed
    {
        get
        {
#if DEBUG
            return Environment.GetEnvironmentVariable("VOICETYPE_DATA_ROOT") is { Length: > 0 };
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Where the pointer files (<see cref="DataLocation"/>) live: the default root, or the sandbox folder in DEBUG sandbox runs
    /// so UI checks never write into the user's real <c>%LOCALAPPDATA%\VoiceType</c>.
    /// </summary>
    public static string PointerRoot => IsDataRootSandboxed ? DataRoot : DefaultDataRoot;

    /// <summary>A line for the App data card: the saved folder was unavailable, or a move did not finish. Empty otherwise.</summary>
    public static string DataRootNote => IsDataRootSandboxed ? DataLocation.ReadNote(DataRoot) : DataLocation.ReadNote(DefaultDataRoot);

    public static string ModelsDir => Path.Combine(DataRoot, "models");
    public static string SessionsDir => Path.Combine(DataRoot, "sessions");
    /// <summary>Top-level history chats: <c>chats/&lt;chatId&gt;/chat.json</c>.</summary>
    public static string ChatsDir => Path.Combine(DataRoot, "chats");
    /// <summary>History job queue: <c>jobs/queue.json</c>.</summary>
    public static string JobsDir => Path.Combine(DataRoot, "jobs");
    public static string JobQueueFile => Path.Combine(JobsDir, "queue.json");
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
    public static string EnsureChatsDir() { var d = ChatsDir; Directory.CreateDirectory(d); return d; }
    public static string EnsureJobsDir() { var d = JobsDir; Directory.CreateDirectory(d); return d; }
    public static string EnsureTempDir() { var d = TempDir; Directory.CreateDirectory(d); return d; }

    private static string ResolveDataRoot()
    {
#if DEBUG
        // Debug-only sandbox: UI checks run against a throwaway data root, never the user's real one.
        if (Environment.GetEnvironmentVariable("VOICETYPE_DATA_ROOT") is { Length: > 0 } sandbox)
            return sandbox;
#endif
        // The pointer file in the default root says where the data really is (Settings › Storage › App data).
        return DataLocation.Resolve(DefaultDataRoot).Root;
    }

    /// <summary>Reset the cached root — only for tests.</summary>
    internal static void ResetForTests() => s_dataRoot = null;
}
