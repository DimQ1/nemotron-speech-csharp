namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Platform abstraction for global hotkey registration.
/// WinUI uses RegisterHotKey/WM_HOTKEY; Linux (X11) will use XGrabKey;
/// Wayland sessions fall back to a compositor portal (not yet implemented).
/// </summary>
public interface IPlatformHotkeyService
{
    /// <summary>Register a hotkey chord (e.g. "Ctrl+Shift+Space"). Returns id or 0 on failure.</summary>
    int Register(string chord);

    /// <summary>Unregister all hotkeys owned by this service.</summary>
    void UnregisterAll();

    /// <summary>Fires when a registered hotkey is pressed. Argument is the registration id.</summary>
    event Action<int>? HotkeyPressed;
}
