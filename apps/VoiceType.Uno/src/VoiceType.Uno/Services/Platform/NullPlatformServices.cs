namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Null Object implementations used until a real platform backend is registered
/// (e.g. on Wayland where global hotkeys/injection need the XDG portal, or in tests).
/// Prevents null checks scattered through ViewModels.
/// </summary>
public sealed class NullHotkeyService : IPlatformHotkeyService
{
#pragma warning disable CS0067 // Event is part of the contract; raised by real backends only
    public event Action<int>? HotkeyPressed;
#pragma warning restore CS0067
    public int Register(string chord) => 0;
    public void UnregisterAll() { }
}

public sealed class NullTextInjector : IPlatformTextInjector
{
    public void Inject(string text) { }
    public void CopyToClipboard(string text) { }
}
