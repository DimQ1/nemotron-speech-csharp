using System.Diagnostics;

namespace VoiceType.Uno.Services.Platform.Linux;

/// <summary>
/// Linux text injector. Coordinates clipboard + keyboard backends:
///
///   Inject(text) — in <see cref="TextInjectionMode.Type"/> (default) types the
///     text straight into the focused window with synthetic key events (keyboard
///     backend picked per session: XTest on X11, ydotool on Wayland), leaving the
///     clipboard alone. Only when the backend cannot type the text exactly
///     (ydotool and non-ASCII) does it set the clipboard and synthesize the paste
///     chord instead. <see cref="TextInjectionMode.Paste"/> always takes that
///     clipboard + chord path.
///   CopyToClipboard(text) — clipboard only, no paste.
///
/// Backends are resolved once; if neither clipboard nor keyboard is available
/// the injector degrades to a no-op with a trace warning.
/// </summary>
public sealed class LinuxTextInjector : IPlatformTextInjector
{
    /// <summary>Typed key by key (default) or pasted through the clipboard; set from the settings.</summary>
    public TextInjectionMode Mode { get; set; } = TextInjectionMode.Type;

    /// <summary>
    /// Paste chord injected after the clipboard is set. Kept as a setting so it
    /// can be overridden (e.g. Shift+Insert in terminals) without a rebuild.
    /// </summary>
    public string PasteChord { get; set; } = "Ctrl+V";

    private readonly ILinuxClipboard _clipboard;
    private readonly ILinuxKeyboard _keyboard;

    // Typing takes real time; overlapping Inject calls must not interleave their text.
    private readonly object _injectGate = new();

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

        lock (_injectGate)
        {
            var canPaste = _clipboard.IsAvailable && _keyboard.IsAvailable;
            var canType = _keyboard.IsAvailable && _keyboard.CanType(text);

            // The user chose "Paste" in the settings.
            if (Mode == TextInjectionMode.Paste && canPaste)
            {
                Paste(text);
                return;
            }

            // Preferred path: type the text directly; the user's clipboard is untouched.
            if (canType)
            {
                _keyboard.TypeText(text);
                return;
            }

            // The backend cannot type this text (ydotool: non-ASCII): paste it instead.
            if (canPaste)
            {
                Paste(text);
                return;
            }

            // Clipboard without keyboard: leave the text in the clipboard and let
            // the user paste manually (matches "Copy" button behavior).
            if (_clipboard.IsAvailable)
                _clipboard.SetText(text);
        }
    }

    private void Paste(string text)
    {
        _clipboard.SetText(text);
        // Give the clipboard owner a beat to take the selection before
        // synthesizing the paste chord in the focused window.
        Thread.Sleep(80);
        _keyboard.PressChord(PasteChord);
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        _clipboard.SetText(text);
    }
}
