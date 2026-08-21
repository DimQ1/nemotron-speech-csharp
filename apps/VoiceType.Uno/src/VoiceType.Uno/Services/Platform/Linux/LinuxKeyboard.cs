using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.Uno.Services.Platform.Linux;

/// <summary>
/// Sends raw text and key chords to the focused window as synthetic key events.
/// </summary>
public interface ILinuxKeyboard
{
    bool IsAvailable { get; }
    void TypeText(string text);
    void PressChord(string chord);
}

public static class LinuxKeyboardFactory
{
    /// <summary>
    /// Creates the best available keyboard backend: XTest on X11, ydotool on
    /// Wayland (any compositor), xdotool as a generic fallback. Never null.
    /// </summary>
    public static ILinuxKeyboard Create()
    {
        if (LinuxSession.IsWayland && ToolLocator.Exists("ydotool"))
            return new YdotoolKeyboard();

        if (LinuxSession.HasX11)
        {
            if (XTestKeyboard.IsSupported())
                return new XTestKeyboard();
            if (ToolLocator.Exists("xdotool"))
                return new XdotoolKeyboard();
        }

        if (ToolLocator.Exists("ydotool"))
            return new YdotoolKeyboard();

        return new NullLinuxKeyboard();
    }
}

internal sealed class NullLinuxKeyboard : ILinuxKeyboard
{
    public bool IsAvailable => false;
    public void TypeText(string text) { }
    public void PressChord(string chord) { }
}

/// <summary>
/// X11 keyboard via libXtst XTestFakeKeyEvent + Xutf8LookupString-free typing:
/// ASCII goes through keysyms; non-ASCII goes through the XKB "Unicode code point"
/// keysym range (0x01000000 + codepoint), remapped onto a spare keycode.
/// </summary>
internal sealed class XTestKeyboard : ILinuxKeyboard
{
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int None = 0;
    private const int CurrentTime = 0;

    public bool IsAvailable => true;

    public static bool IsSupported()
    {
        try
        {
            var display = XOpenDisplay(nint.Zero);
            if (display == nint.Zero)
                return false;
            XCloseDisplay(display);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void TypeText(string text)
    {
        var display = XOpenDisplay(nint.Zero);
        if (display == nint.Zero)
            return;

        try
        {
            foreach (var rune in text.EnumerateRunes())
                TypeRune(display, rune);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    public void PressChord(string chord)
    {
        var display = XOpenDisplay(nint.Zero);
        if (display == nint.Zero)
            return;

        try
        {
            var (modifiers, key) = LinuxKeyMap.ParseChord(chord);
            foreach (var mod in modifiers)
                SendKeysym(display, LinuxKeyMap.ModifierKeysym(mod), true);
            SendKeysym(display, LinuxKeyMap.KeyKeysym(key), true);
            SendKeysym(display, LinuxKeyMap.KeyKeysym(key), false);
            foreach (var mod in modifiers.AsEnumerable().Reverse())
                SendKeysym(display, LinuxKeyMap.ModifierKeysym(mod), false);
            XFlush(display);
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static void TypeRune(nint display, Rune rune)
    {
        // ASCII printable + control chars map directly to their keysym value.
        if (rune.Value < 0x80)
        {
            SendKeysym(display, (nuint)AsciiKeysym(rune.Value), true);
            SendKeysym(display, (nuint)AsciiKeysym(rune.Value), false);
            return;
        }

        // Unicode: remap a spare keycode (250-254 range is unused on XKB) to a
        // Unicode codepoint keysym (0x01000000 + cp), then press/release it.
        var codepoint = rune.Value;
        var unicodeKeysym = (nuint)(0x01000000u + (uint)codepoint);
        var keycode = 250 + (codepoint % 5);
        nint keysymsPtr = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(keysymsPtr, (nint)unicodeKeysym);
            XChangeKeyboardMapping(display, keycode, 1, keysymsPtr, 1);
            XSync(display, false);
            XTestFakeKeyEvent(display, (uint)keycode, true, CurrentTime);
            XTestFakeKeyEvent(display, (uint)keycode, false, CurrentTime);
        }
        finally
        {
            Marshal.FreeHGlobal(keysymsPtr);
        }
    }

    private static int AsciiKeysym(int value) => value switch
    {
        '\n' or '\r' => 0xFF0D, // XK_Return
        '\t' => 0xFF09,         // XK_Tab
        _ => value              // Latin-1 keysyms equal the character code
    };

    private static void SendKeysym(nint display, nuint keysym, bool press)
    {
        var keycode = XKeysymToKeycode(display, keysym);
        if (keycode == 0)
            return;
        XTestFakeKeyEvent(display, keycode, press, CurrentTime);
    }

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint XOpenDisplay(nint displayName);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XFlush(nint display);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XSync(nint display, bool discard);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern byte XKeysymToKeycode(nint display, nuint keysym);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XChangeKeyboardMapping(nint display, int firstKeycode, int keysymsPerKeycode, nint keysyms, int numCodes);

    [DllImport("libXtst.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XTestFakeKeyEvent(nint display, uint keycode, bool isPress, nuint delay);
}

/// <summary>
/// Wayland keyboard via ydotool (uinput-based, compositor-independent).
/// Requires the ydotoold daemon running and the user in the input group.
/// </summary>
internal sealed class YdotoolKeyboard : ILinuxKeyboard
{
    public bool IsAvailable => true;

    public void TypeText(string text) =>
        RunYdotool(["type", "--key-delay", "1", "--", text]);

    public void PressChord(string chord) =>
        RunYdotool(["key", "--", LinuxKeyMap.ToYdotoolChord(chord)]);

    private static void RunYdotool(string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ydotool",
                Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VoiceType.Uno] ydotool failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Generic fallback keyboard via xdotool (works on X11; on Wayland only within
/// XWayland windows). Used when libXtst is unavailable.
/// </summary>
internal sealed class XdotoolKeyboard : ILinuxKeyboard
{
    public bool IsAvailable => true;

    public void TypeText(string text) =>
        RunXdotool(["type", "--delay", "1", "--clearmodifiers", "--", text]);

    public void PressChord(string chord) =>
        RunXdotool(["key", "--clearmodifiers", "--", LinuxKeyMap.ToXdotoolChord(chord)]);

    private static void RunXdotool(string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "xdotool",
                Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VoiceType.Uno] xdotool failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Parses "Ctrl+Shift+V" style chords into modifier tokens + key token and
/// maps them to X keysyms / ydotool key names / xdotool chord strings.
/// </summary>
public static class LinuxKeyMap
{
    private static readonly Dictionary<string, string> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = "ctrl", ["Control"] = "ctrl",
        ["Shift"] = "shift",
        ["Alt"] = "alt",
        ["Super"] = "super", ["Win"] = "super", ["Meta"] = "super"
    };

    private static readonly Dictionary<string, nuint> ModifierKeysyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0xFFE3,   // XK_Control_L
        ["shift"] = 0xFFE1,  // XK_Shift_L
        ["alt"] = 0xFFE9,    // XK_Alt_L
        ["super"] = 0xFFEB   // XK_Super_L
    };

    private static readonly Dictionary<string, (nuint keysym, string ydotool, string xdotool)> Keys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["V"] = (0x0076, "47", "v"),       // KEY_V = 47
            ["M"] = (0x006D, "50", "m"),       // KEY_M = 50
            ["I"] = (0x0069, "23", "i"),       // KEY_I = 23
            ["Space"] = (0x0020, "57", "space"),
            ["Enter"] = (0xFF0D, "28", "Return"),
            ["Tab"] = (0xFF09, "15", "Tab"),
            ["Escape"] = (0xFF1B, "1", "Escape")
        };

    // evdev keycodes for modifiers (ydotool uses Linux input event codes).
    private static readonly Dictionary<string, string> YdotoolModifiers = new(StringComparer.Ordinal)
    {
        ["ctrl"] = "29",   // KEY_LEFTCTRL
        ["shift"] = "42",  // KEY_LEFTSHIFT
        ["alt"] = "56",    // KEY_LEFTALT
        ["super"] = "125"  // KEY_LEFTMETA
    };

    public static (List<string> modifiers, string key) ParseChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<string>();
        var key = "";
        foreach (var part in parts)
        {
            if (Modifiers.TryGetValue(part, out var mod))
            {
                if (!modifiers.Contains(mod))
                    modifiers.Add(mod);
            }
            else
            {
                key = part;
            }
        }
        return (modifiers, key);
    }

    public static nuint ModifierKeysym(string modifier) =>
        ModifierKeysyms.TryGetValue(modifier, out var keysym) ? keysym : 0xFFE3;

    public static nuint KeyKeysym(string key) =>
        Keys.TryGetValue(key, out var entry)
            ? entry.keysym
            : key.Length == 1 ? (nuint)char.ToLowerInvariant(key[0]) : 0x0020;

    /// <summary>ydotool chord: "29:1 42:1 47:1 47:0 42:0 29:0" (press all, release all).</summary>
    public static string ToYdotoolChord(string chord)
    {
        var (modifiers, key) = ParseChord(chord);
        var codes = modifiers
            .Select(m => YdotoolModifiers.TryGetValue(m, out var code) ? code : "29")
            .ToList();
        var keyCode = Keys.TryGetValue(key, out var entry) ? entry.ydotool : "57";
        codes.Add(keyCode);

        var sb = new StringBuilder();
        foreach (var code in codes)
            sb.Append(code).Append(":1 ");
        for (var i = codes.Count - 1; i >= 0; i--)
            sb.Append(codes[i]).Append(":0 ");
        return sb.ToString().TrimEnd();
    }

    /// <summary>xdotool chord: "ctrl+shift+v".</summary>
    public static string ToXdotoolChord(string chord)
    {
        var (modifiers, key) = ParseChord(chord);
        var keyName = Keys.TryGetValue(key, out var entry) ? entry.xdotool : key.ToLowerInvariant();
        return string.Join('+', modifiers.Append(keyName));
    }
}
