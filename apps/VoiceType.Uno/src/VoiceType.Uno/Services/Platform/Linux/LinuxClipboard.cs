using System.Diagnostics;

namespace VoiceType.Uno.Services.Platform.Linux;

/// <summary>
/// Sets the Linux clipboard via external utilities (wl-copy / xclip / xsel),
/// picked to match the session type. External tools are used instead of the
/// freedesktop Clipboard portal because major compositors (GNOME, KDE) do not
/// implement the clipboard portal for arbitrary apps.
/// </summary>
public interface ILinuxClipboard
{
    bool IsAvailable { get; }
    void SetText(string text);
}

public static class LinuxClipboardFactory
{
    /// <summary>
    /// Creates the best available clipboard backend. Never returns null —
    /// falls back to <see cref="NullLinuxClipboard"/>.
    /// </summary>
    public static ILinuxClipboard Create()
    {
        var external = ExternalLinuxClipboard.TryCreate();
        if (external is not null)
            return external;

        return NullLinuxClipboard.Instance;
    }
}

internal sealed class NullLinuxClipboard : ILinuxClipboard
{
    public static readonly NullLinuxClipboard Instance = new();
    public bool IsAvailable => false;
    public void SetText(string text) { }
}

/// <summary>
/// External-tool clipboard: wl-copy (Wayland), xclip or xsel (X11).
/// </summary>
internal sealed class ExternalLinuxClipboard : ILinuxClipboard
{
    private readonly string _tool;
    private readonly string _args;

    private ExternalLinuxClipboard(string tool, string args)
    {
        _tool = tool;
        _args = args;
    }

    public bool IsAvailable => true;

    public static ExternalLinuxClipboard? TryCreate()
    {
        // Prefer a tool matching the session; any of them also work through XWayland.
        if (LinuxSession.IsWayland && ToolLocator.Exists("wl-copy"))
            return new ExternalLinuxClipboard("wl-copy", "");
        if (LinuxSession.HasX11 && ToolLocator.Exists("xclip"))
            return new ExternalLinuxClipboard("xclip", "-selection clipboard -in");
        if (LinuxSession.HasX11 && ToolLocator.Exists("xsel"))
            return new ExternalLinuxClipboard("xsel", "--clipboard --input");
        if (ToolLocator.Exists("wl-copy"))
            return new ExternalLinuxClipboard("wl-copy", "");
        return null;
    }

    public void SetText(string text)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _tool,
                    Arguments = _args,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.StandardInput.Write(text);
            process.StandardInput.Close();
            // wl-copy stays alive to serve the selection — don't wait indefinitely.
            if (!process.WaitForExit(500))
            {
                try { process.Kill(entireProcessTree: false); }
                catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VoiceType.Uno] Clipboard tool '{_tool}' failed: {ex.Message}");
        }
    }
}

internal static class ToolLocator
{
    public static bool Exists(string tool)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/which",
                Arguments = tool,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;
            process.WaitForExit(2000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
