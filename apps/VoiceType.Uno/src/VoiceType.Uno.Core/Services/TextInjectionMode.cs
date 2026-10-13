namespace VoiceType.Uno.Services;

/// <summary>How the platform injector gets a phrase into the focused window (the "Typing" settings).</summary>
public enum TextInjectionMode
{
    /// <summary>Sent as key presses; the clipboard is not touched.</summary>
    Type,

    /// <summary>Put on the clipboard, then the paste shortcut is sent.</summary>
    Paste,
}
