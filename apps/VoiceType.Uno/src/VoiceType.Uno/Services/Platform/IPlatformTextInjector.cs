namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Platform abstraction for injecting transcribed text into the focused window.
/// Windows types it with SendInput; Linux types it with XTest (X11) or ydotool (Wayland)
/// and pastes through the clipboard only where typing is impossible.
/// </summary>
public interface IPlatformTextInjector
{
    /// <summary>
    /// Type text into the currently focused input field as synthetic key events,
    /// without touching the clipboard. May block while the keys are delivered.
    /// </summary>
    void Inject(string text);

    /// <summary>Copy text to the system clipboard without pasting.</summary>
    void CopyToClipboard(string text);

    /// <summary>
    /// False where text cannot be typed into other apps (Android, macOS): the
    /// "Inject text" mode is hidden there.
    /// </summary>
    bool CanInject => true;

    /// <summary>
    /// True when the focused window belongs to another app, i.e. when typing reaches the app the user is dictating
    /// into. False while the focus is inside VoiceType itself (a typed space or Enter would press its focused
    /// button) or nothing has the focus. Platforms that cannot tell answer true.
    /// </summary>
    bool IsFocusedWindowForeign() => true;

    /// <summary>
    /// How <see cref="Inject"/> delivers text: typed key by key (default) or pasted through the
    /// clipboard. Set from the settings; injectors without a choice ignore it.
    /// </summary>
    TextInjectionMode Mode { get => TextInjectionMode.Type; set { } }

    /// <summary>Shortcut sent in paste mode ("Ctrl+V", "Ctrl+Shift+V", "Shift+Insert"). Set from the settings.</summary>
    string PasteChord { get => "Ctrl+V"; set { } }
}
