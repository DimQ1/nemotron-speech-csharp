using System.Diagnostics;

namespace VoiceType.Uno.Services.Platform.Linux;

/// <summary>
/// Linux text injector. Coordinates clipboard + keyboard backends:
///
///   Inject(text) — sets the clipboard, then synthesizes Ctrl+V in the focused
///     window (keyboard backend picked per session: ydotool on Wayland, XTest
///     on X11). Falls back to direct typing when no paste path is possible.
///   CopyToClipboard(text) — clipboard only, no paste.
///
/// Backends are resolved once; if neither clipboard nor keyboard is available
/// the injector degrades to a no-op with a trace warning.
/// </summary>
public sealed class LinuxTextInjector : IPlatformTextInjector
{
    /// <summary>
    /// Paste chord injected after the clipboard is set. Kept as a setting so it
    /// can be overridden (e.g. Shift+Insert in terminals) without a rebuild.
    /// </summary>
    public string PasteChord { get; set; } = "Ctrl+V";

    private readonly ILinuxClipboard _clipboard;
    private readonly ILinuxKeyboard _keyboard;

    public LinuxTextInjector()
        : this(LinuxClipboardFactory.Create(), LinuxKeyboardFactory.Create())
    {
    }

    public LinuxTextInjector(ILinuxClipboard clipboard, ILinuxKeyboard keyboard)
    {
        _clipboard = clipboard;
        _keyboard = keyboard;

        if (!_clipboard.IsAvailable && !_keyboard.IsAvailable)
        {
            Trace.WriteLine(
                "[VoiceType.Uno] Text injection unavailable: no clipboard tool " +
                "(wl-copy/xclip/xsel) and no keyboard backend (ydotool/libXtst/xdotool) found. " +
                LinuxSession.Describe());
        }
    }

    /// <summary>True when at least one injection path is available.</summary>
    public bool IsAvailable => _clipboard.IsAvailable || _keyboard.IsAvailable;

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Preferred path: clipboard + synthetic paste — fast, exact for Unicode,
        // and does not depend on per-key layout mapping.
        if (_clipboard.IsAvailable && _keyboard.IsAvailable)
        {
            _clipboard.SetText(text);
            // Give the clipboard owner a beat to take the selection before
            // synthesizing the paste chord in the focused window.
            Thread.Sleep(80);
            _keyboard.PressChord(PasteChord);
            return;
        }

        // Clipboard without keyboard: leave the text in the clipboard and let
        // the user paste manually (matches "Copy" button behavior).
        if (_clipboard.IsAvailable)
        {
            _clipboard.SetText(text);
            return;
        }

        // Keyboard only: type the text directly.
        if (_keyboard.IsAvailable)
            _keyboard.TypeText(text);
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _clipboard.SetText(text);
    }
}
