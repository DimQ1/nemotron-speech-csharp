using System.Runtime.CompilerServices;

namespace VoiceType.Uno.Tests;

/// <summary>
/// Points <c>AppPaths.DataRoot</c> (Debug builds honour <c>VOICETYPE_DATA_ROOT</c>) at a throwaway folder before any test touches it,
/// so no test can read or move the models in the user's real <c>%LOCALAPPDATA%\VoiceType</c>.
/// </summary>
internal static class TestDataRoot
{
    [ModuleInitializer]
    internal static void Init()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICETYPE_DATA_ROOT")))
            Environment.SetEnvironmentVariable("VOICETYPE_DATA_ROOT",
                Path.Combine(Path.GetTempPath(), "voicetype-uno-tests", "data-root-" + Environment.ProcessId));
    }
}
