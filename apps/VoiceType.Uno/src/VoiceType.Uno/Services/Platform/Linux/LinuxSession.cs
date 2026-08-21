namespace VoiceType.Uno.Services.Platform.Linux;

/// <summary>
/// Detects the Linux session type (Wayland vs X11) from environment variables.
/// </summary>
public static class LinuxSession
{
    /// <summary>True when running under a Wayland session.</summary>
    public static bool IsWayland =>
        string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>True when an X display is reachable (X11 session or XWayland).</summary>
    public static bool HasX11 => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static string Describe() =>
        $"XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)"}, " +
        $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(unset)"}, " +
        $"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "(unset)"}";
}
