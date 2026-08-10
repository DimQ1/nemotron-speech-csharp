using System.Text;

namespace VoiceType.Hotkeys;

/// <summary>
/// Parses app-style chords ("Ctrl+Shift+Space") into a modifier mask + key token
/// that platform backends translate to their native representation
/// (XDG portal modifier bits, X11 keysyms, Win32 MOD_*).
/// </summary>
public readonly record struct HotkeyChord(bool Ctrl, bool Shift, bool Alt, bool Super, string Key)
{
    // XDG portal shortcut modifier bits (org.freedesktop.impl.portal.GlobalShortcuts spec)
    public const uint PortalShift = 1;
    public const uint PortalCtrl = 2;
    public const uint PortalAlt = 4;
    public const uint PortalSuper = 8;

    public uint PortalModifiers =>
        (Shift ? PortalShift : 0u) |
        (Ctrl ? PortalCtrl : 0u) |
        (Alt ? PortalAlt : 0u) |
        (Super ? PortalSuper : 0u);

    /// <summary>Parse "Ctrl+Shift+Space" (separator '+', case-insensitive modifiers).</summary>
    public static bool TryParse(string chord, out HotkeyChord result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(chord))
            return false;

        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        bool ctrl = false, shift = false, alt = false, super = false;
        var keyIndex = parts.Length;

        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control" or "ctl": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                case "super" or "win" or "meta" or "cmd": super = true; break;
                default:
                    if (keyIndex != parts.Length)
                        return false; // second non-modifier token → invalid
                    keyIndex = i;
                    break;
            }
        }

        if (keyIndex == parts.Length)
            return false; // modifiers only, no key

        result = new HotkeyChord(ctrl, shift, alt, super, NormalizeKey(parts[keyIndex]));
        return true;
    }

    /// <summary>Canonical token used by backends ("Space", "F5", "A").</summary>
    private static string NormalizeKey(string key) => key.Length == 1
        ? key.ToUpperInvariant()
        : string.Concat(key[..1].ToUpperInvariant(), key[1..].ToLowerInvariant());

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Ctrl) sb.Append("Ctrl+");
        if (Shift) sb.Append("Shift+");
        if (Alt) sb.Append("Alt+");
        if (Super) sb.Append("Super+");
        sb.Append(Key);
        return sb.ToString();
    }
}
