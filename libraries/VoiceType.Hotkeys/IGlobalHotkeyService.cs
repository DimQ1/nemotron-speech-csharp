namespace VoiceType.Hotkeys;

/// <summary>
/// Cross-platform global hotkey registration.
/// Implementations: XDG GlobalShortcuts portal (Linux), RegisterHotKey (Windows),
/// <see cref="NullGlobalHotkeyService"/> for unsupported environments/tests.
/// </summary>
public interface IGlobalHotkeyService : IAsyncDisposable
{
    /// <summary>
    /// Register a hotkey chord (e.g. "Ctrl+Shift+Space").
    /// Returns a registration id (> 0), or 0 when the chord could not be registered
    /// (invalid chord, already grabbed by another client, portal denied).
    /// </summary>
    Task<int> RegisterAsync(string chord, CancellationToken cancellationToken = default);

    /// <summary>Unregister all hotkeys owned by this service.</summary>
    Task UnregisterAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Fires when a registered hotkey is pressed. Argument is the registration id.</summary>
    event Action<int>? HotkeyPressed;

    /// <summary>
    /// True when the backend can actually deliver hotkeys in this session
    /// (portal available, compositor supports the interface). False → the app
    /// should show hotkeys as unavailable instead of failing silently.
    /// </summary>
    bool IsAvailable { get; }
}
