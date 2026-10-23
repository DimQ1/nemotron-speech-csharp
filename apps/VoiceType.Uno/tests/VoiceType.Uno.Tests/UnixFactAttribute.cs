using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>A fact that only runs on Linux and macOS (it starts a <c>/bin/sh</c> script); elsewhere it is reported as skipped.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string reason = "Linux/macOS only: the test runs a /bin/sh script.")
    {
        if (OperatingSystem.IsWindows())
            Skip = reason;
    }
}
