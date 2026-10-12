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
    public bool CanInject => false;

    public void Inject(string text) { }

    /// <summary>
    /// Copies through the WinUI clipboard API, which Uno implements on every head
    /// (Android ClipboardManager included). Must be called on the UI thread.
    /// </summary>
    public void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}
