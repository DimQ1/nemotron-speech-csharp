namespace VoiceType.Uno.Services;

/// <summary>The three global shortcuts of the app.</summary>
public enum HotkeyAction { ToggleRecording, ToggleMute, InjectText }

/// <summary>Shortcut names, key chips, chord validation and the registration-error texts (design §3.3, hotkeys screens).</summary>
public static class HotkeyText
{
    public const string Help = "Select a field and press the new combination. Esc clears it. Shortcuts need at least one of Ctrl, Alt or Win.";
    public const string RecordingPrompt = "Press keys… Esc clears";
    public const string NeedsModifier = "A shortcut needs at least one of Ctrl, Alt or Win.";
    public const string NoShortcut = "No shortcut";

    /// <summary>The row's second line when a shortcut could not be registered.</summary>
    public const string NotRegistered = "Not registered";

    public static string Title(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleRecording => "Start or stop recording",
        HotkeyAction.ToggleMute => "Mute microphone",
        _ => "Type the transcript",
    };

    public static string Detail(HotkeyAction action) => action switch
    {
        HotkeyAction.InjectText => "Into the focused app",
        _ => "",
    };

    /// <summary>What an action is called inside the "has no shortcut" sentence.</summary>
    private static string ShortName(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleRecording => "Start/stop",
        HotkeyAction.ToggleMute => "Mute",
        _ => "Type transcript",
    };

    public static string Chord(AppSettings settings, HotkeyAction action) => (action switch
    {
        HotkeyAction.ToggleRecording => settings.ToggleHotkey,
        HotkeyAction.ToggleMute => settings.MuteHotkey,
        _ => settings.InjectTextHotkey,
    }) ?? "";

    public static void SetChord(AppSettings settings, HotkeyAction action, string chord)
    {
        switch (action)
        {
            case HotkeyAction.ToggleRecording: settings.ToggleHotkey = chord; break;
            case HotkeyAction.ToggleMute: settings.MuteHotkey = chord; break;
            default: settings.InjectTextHotkey = chord; break;
        }
    }

    /// <summary><c>Ctrl+Shift+V</c> gives <c>Ctrl</c>, <c>Shift</c>, <c>V</c>; empty for no shortcut.</summary>
    public static IReadOnlyList<string> Chips(string? chord) =>
        string.IsNullOrWhiteSpace(chord)
            ? []
            : chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => string.Equals(k, "Super", StringComparison.OrdinalIgnoreCase) ? "Win" : k)
                .ToList();

    /// <summary>Joins pressed modifiers and a key into the stored chord form (<c>Ctrl+Shift+V</c>); modifiers come first in a fixed order.</summary>
    public static string Compose(bool ctrl, bool shift, bool alt, bool win, string key)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        if (win) parts.Add("Super");
        parts.Add(key);
        return string.Join('+', parts);
    }

    /// <summary>Null when the chord can be registered; otherwise the reason. An empty chord clears the shortcut and is fine.</summary>
    public static string? Validate(string? chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return null;
        var keys = Chips(chord);
        var hasModifier = keys.Take(keys.Count - 1).Any(k =>
            k.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
            || k.Equals("Alt", StringComparison.OrdinalIgnoreCase)
            || k.Equals("Win", StringComparison.OrdinalIgnoreCase));
        return hasModifier ? null : NeedsModifier;
    }

    /// <summary>The other action that already has this chord, else null.</summary>
    public static HotkeyAction? DuplicateOf(AppSettings settings, HotkeyAction action, string chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
            return null;
        foreach (var other in Enum.GetValues<HotkeyAction>())
            if (other != action && string.Equals(Chord(settings, other), chord, StringComparison.OrdinalIgnoreCase))
                return other;
        return null;
    }

    public static string DuplicateMessage(string chord, HotkeyAction other) => $"{chord} is already used for “{Title(other)}”.";

    /// <summary>
    /// The banner after a registration failure: <c>Ctrl+Shift+M is taken by another app. Mute has no shortcut until you choose a
    /// different one.</c> Empty when nothing failed.
    /// </summary>
    public static string ConflictBanner(IReadOnlyList<(HotkeyAction Action, string Chord)> failed)
    {
        if (failed.Count == 0)
            return "";
        var chords = string.Join(", ", failed.Select(f => f.Chord));
        var names = string.Join(", ", failed.Select(f => ShortName(f.Action)));
        return failed.Count == 1
            ? $"{chords} is taken by another app. {names} has no shortcut until you choose a different one."
            : $"{chords} are taken by other apps. {names} have no shortcut until you choose different ones.";
    }

    /// <summary>The Keyboard shortcuts line in the hub: <c>Ctrl+Shift+V starts and stops</c>.</summary>
    public static string Summary(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ToggleHotkey) ? "No shortcut to start and stop" : settings.ToggleHotkey.Trim() + " starts and stops";
}
