namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Null Object implementations used until a real platform backend is registered
/// (e.g. where text injection needs XTest/ydotool, or in tests).
/// Prevents null checks scattered through ViewModels.
/// Note: global hotkeys now live in the VoiceType.Hotkeys library
/// (XDG GlobalShortcuts portal on Linux, NullGlobalHotkeyService fallback).
/// </summary>
public sealed class NullTextInjector : IPlatformTextInjector
{
    public void Inject(string text) { }
    public void CopyToClipboard(string text) { }
}
