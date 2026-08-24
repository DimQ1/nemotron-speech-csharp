namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Platform abstraction for injecting transcribed text into the focused window.
/// WinUI uses SendInput/clipboard; Linux uses XTest (X11) or clipboard fallback.
/// </summary>
public interface IPlatformTextInjector
{
    /// <summary>Inject text into the currently focused input field.</summary>
    void Inject(string text);

    /// <summary>Copy text to the system clipboard without pasting.</summary>
    void CopyToClipboard(string text);
}
