using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>
/// A fact that only runs on Windows, for behaviour that exists only there: a file held open with
/// <see cref="FileShare.None"/> blocks renaming and deleting it on Windows, while on Linux a rename or an unlink
/// ignores open handles, so "a locked item stays behind" cannot be produced. Elsewhere the test is reported as skipped.
/// </summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string reason = "Windows only: an open file blocks rename and delete only on Windows.")
    {
        if (!OperatingSystem.IsWindows())
            Skip = reason;
    }
}
