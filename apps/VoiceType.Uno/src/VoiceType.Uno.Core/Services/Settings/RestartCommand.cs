namespace VoiceType.Uno.Services;

/// <summary>"Restart now": how to start the app again (desktop heads).</summary>
public static class RestartCommand
{
    /// <summary>Environment variable carrying the old process id; the new process waits for it to exit before it moves any folder.</summary>
    public const string ParentPidVariable = "VOICETYPE_RESTART_PARENT_PID";

    /// <summary>
    /// The program and arguments that start this app again. A framework-dependent launch (<c>dotnet App.dll</c>) passes the
    /// dll as the first argument; a native host (<c>App.exe</c>) passes only the arguments after the program name.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments)? Build(string? processPath, IReadOnlyList<string> commandLineArgs)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return null;
        var viaDotnet = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        // GetCommandLineArgs()[0] is the program (or, under "dotnet", the dll), so only the native host skips it.
        var args = commandLineArgs.Skip(viaDotnet ? 0 : 1).ToList();
        return (processPath, args);
    }
}
