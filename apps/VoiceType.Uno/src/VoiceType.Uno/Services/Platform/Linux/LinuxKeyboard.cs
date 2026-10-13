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

    /// <summary>
    /// False when this backend cannot type <paramref name="text"/> exactly (ydotool has no
    /// keymap for non-ASCII characters); the caller then pastes through the clipboard.
    /// </summary>
    bool CanType(string text) => true;

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
/// X11 keyboard via libXtst XTestFakeKeyEvent. Text is typed the way xdotool does it:
/// every character becomes a keysym (Latin-1 as is, anything else in the "Unicode code
/// point" range 0x01000000 + codepoint) that is bound to a scratch keycode — one that
/// no key produces — and that keycode is pressed. A key bound to { ks, ks } yields the
/// same character on any layout or group and ignores Shift/CapsLock, so the result does
/// not depend on the active layout (Cyrillic on a US layout included).
/// </summary>
internal sealed class XTestKeyboard : ILinuxKeyboard
{
    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int None = 0;
    private const int CurrentTime = 0;

    // Scratch keycodes are claimed once per process and stay bound afterwards: a keysym
    // that is still bound costs nothing on the next call, and unbinding right after the
    // last key would race with clients that re-read the keymap asynchronously and then
    // lose characters.
    private const int ScratchPoolSize = 16;
    private const int BindSettleMs = 40;
    private const int ChunkGapMs = 12;
    private const int KeyGapMs = 1;

    private static readonly object TypeGate = new();
    private static byte[]? _scratch;
    private static nuint[] _slotKeysym = [];
    private static int _nextSlot;

    // Shift/Control/Meta/Alt/Super/Hyper (left+right) and ISO_Level3_Shift (AltGr).
    private static readonly nuint[] ModifierKeysyms =
        [0xFFE1, 0xFFE2, 0xFFE3, 0xFFE4, 0xFFE7, 0xFFE8, 0xFFE9, 0xFFEA, 0xFFEB, 0xFFEC, 0xFFED, 0xFFEE, 0xFE03];

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
        var keysyms = ToKeysyms(text);
        if (keysyms.Count == 0)
            return;

        // One typist at a time: two overlapping calls would interleave their characters
        // and fight over the scratch keycodes.
        lock (TypeGate)
        {
            var display = XOpenDisplay(nint.Zero);
            if (display == nint.Zero)
                return;

            try
            {
                if (!EnsureScratchPool(display))
                {
                    Trace.WriteLine("[VoiceType.Uno] XTest: no unused keycode available to type with");
                    return;
                }

                ReleaseHeldModifiers(display);

                var chunks = SplitIntoChunks(keysyms, _scratch!.Length);
                for (var i = 0; i < chunks.Count; i++)
                {
                    // Let clients drain the previous chunk before its keycodes are rebound.
                    if (i > 0)
                        Thread.Sleep(ChunkGapMs);
                    TypeChunk(display, chunks[i]);
                }
                XSync(display, false);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
    }

    /// <summary>Keysym per character; "\r\n" is one Enter, other control characters are skipped.</summary>
    internal static List<nuint> ToKeysyms(string text)
    {
        var keysyms = new List<nuint>(text.Length);
        var previous = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (!(value == '\n' && previous == '\r'))
            {
                var keysym = KeysymFor(value);
                if (keysym != 0)
                    keysyms.Add(keysym);
            }
            previous = value;
        }
        return keysyms;
    }

    internal static nuint KeysymFor(int codepoint) => codepoint switch
    {
        '\n' or '\r' => 0xFF0D,              // XK_Return
        '\t' => 0xFF09,                      // XK_Tab
        < 0x20 or (>= 0x7F and < 0xA0) => 0, // other control characters
        < 0x100 => (nuint)codepoint,         // Latin-1 keysyms equal the character code
        _ => (nuint)(0x01000000 + codepoint) // Unicode keysym range
    };

    /// <summary>
    /// Splits the keysym sequence into runs with at most <paramref name="poolSize"/> distinct
    /// keysyms, so every keysym of a run can be bound to its own scratch keycode at once.
    /// </summary>
    internal static List<List<nuint>> SplitIntoChunks(List<nuint> keysyms, int poolSize)
    {
        var chunks = new List<List<nuint>>();
        var current = new List<nuint>();
        var distinct = new HashSet<nuint>();
        foreach (var keysym in keysyms)
        {
            if (!distinct.Contains(keysym) && distinct.Count == poolSize)
            {
                chunks.Add(current);
                current = [];
                distinct.Clear();
            }
            distinct.Add(keysym);
            current.Add(keysym);
        }
        if (current.Count > 0)
            chunks.Add(current);
        return chunks;
    }

    private static void TypeChunk(nint display, List<nuint> chunk)
    {
        BindKeysyms(display, new HashSet<nuint>(chunk));

        foreach (var keysym in chunk)
        {
            var keycode = _scratch![Array.IndexOf(_slotKeysym, keysym)];
            XTestFakeKeyEvent(display, keycode, true, CurrentTime);
            XTestFakeKeyEvent(display, keycode, false, CurrentTime);
            XFlush(display);
            // Identical timestamps on release+press of one keycode look like auto-repeat to some toolkits.
            Thread.Sleep(KeyGapMs);
        }
        XSync(display, false);
    }

    /// <summary>Binds every keysym in <paramref name="needed"/> that is not bound yet to a free scratch keycode.</summary>
    private static void BindKeysyms(nint display, HashSet<nuint> needed)
    {
        var changed = false;
        foreach (var keysym in needed)
        {
            if (Array.IndexOf(_slotKeysym, keysym) >= 0)
                continue;

            // Round-robin over the slots, skipping the ones this chunk still needs.
            int slot;
            do
            {
                slot = _nextSlot;
                _nextSlot = (_nextSlot + 1) % _slotKeysym.Length;
            }
            while (needed.Contains(_slotKeysym[slot]));

            XChangeKeyboardMapping(display, _scratch![slot], 2, [keysym, keysym], 1);
            _slotKeysym[slot] = keysym;
            changed = true;
        }

        if (changed)
        {
            XSync(display, false);
            Thread.Sleep(BindSettleMs);
        }
    }

    /// <summary>
    /// Claims up to <see cref="ScratchPoolSize"/> keycodes: unused ones, plus the leftovers of an
    /// earlier run that was killed before it could unbind them (see <see cref="IsLeftoverBinding"/>),
    /// so restarts do not use up the server's free keycodes.
    /// </summary>
    private static bool EnsureScratchPool(nint display)
    {
        if (_scratch is not null)
            return _scratch.Length > 0;

        XDisplayKeycodes(display, out var min, out var max);
        var count = max - min + 1;
        var map = XGetKeyboardMapping(display, (byte)min, count, out var perKeycode);
        if (map == nint.Zero)
            return false;

        var claimed = new List<byte>();
        try
        {
            // From the top: the highest keycodes are the least likely to be real keys.
            for (var code = max; code >= min && claimed.Count < ScratchPoolSize; code--)
            {
                var entries = new nuint[perKeycode];
                for (var j = 0; j < perKeycode; j++)
                    entries[j] = (nuint)Marshal.ReadIntPtr(map, ((code - min) * perKeycode + j) * IntPtr.Size);

                if (entries.All(e => e == 0) || IsLeftoverBinding(entries))
                    claimed.Add((byte)code);
            }
        }
        finally
        {
            XFree(map);
        }

        _scratch = claimed.ToArray();
        _slotKeysym = new nuint[_scratch.Length];
        if (_scratch.Length > 0)
            AppDomain.CurrentDomain.ProcessExit += (_, _) => UnbindScratchPool();
        return _scratch.Length > 0;
    }

    /// <summary>
    /// A scratch binding reads back as one typable keysym repeated across the first columns
    /// (the server replicates { ks, ks } into every group) and NoSymbol after that. No real
    /// key looks like that: letters carry two different symbols per group and the keys that
    /// repeat a symbol (XF86, media) leave the second column empty or use non-typable keysyms.
    /// </summary>
    internal static bool IsLeftoverBinding(nuint[] entries) =>
        entries.Length >= 2
        && entries[0] != 0
        && entries[0] == entries[1]
        && entries.All(e => e == 0 || e == entries[0])
        && entries[0] is (>= 0x20 and <= 0xFF) or (>= 0x01000100 and <= 0x0110FFFF) or 0xFF0D or 0xFF09;

    /// <summary>Gives the scratch keycodes back (best effort, at process exit).</summary>
    private static void UnbindScratchPool()
    {
        lock (TypeGate)
        {
            if (_scratch is null || _scratch.Length == 0)
                return;
            try
            {
                var display = XOpenDisplay(nint.Zero);
                if (display == nint.Zero)
                    return;
                try
                {
                    foreach (var keycode in _scratch)
                        XChangeKeyboardMapping(display, keycode, 2, [0, 0], 1);
                    XSync(display, false);
                }
                finally
                {
                    XCloseDisplay(display);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[VoiceType.Uno] XTest: releasing scratch keycodes failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Releases modifiers the user still holds (typically the hotkey that triggered the
    /// injection): with Ctrl or Alt down the characters would arrive as shortcuts. Like
    /// xdotool --clearmodifiers, minus restoring them afterwards, which would leave a
    /// modifier stuck if the user let go in the meantime.
    /// </summary>
    private static void ReleaseHeldModifiers(nint display)
    {
        var pressed = new byte[32];
        XQueryKeymap(display, pressed);

        var released = false;
        foreach (var keysym in ModifierKeysyms)
        {
            var keycode = XKeysymToKeycode(display, keysym);
            if (keycode == 0 || (pressed[keycode >> 3] & (1 << (keycode & 7))) == 0)
                continue;
            XTestFakeKeyEvent(display, keycode, false, CurrentTime);
            released = true;
        }

        if (released)
        {
            XSync(display, false);
            Thread.Sleep(BindSettleMs);
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
    private static extern int XChangeKeyboardMapping(nint display, int firstKeycode, int keysymsPerKeycode, nuint[] keysyms, int numCodes);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XDisplayKeycodes(nint display, out int minKeycodes, out int maxKeycodes);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint XGetKeyboardMapping(nint display, byte firstKeycode, int keycodeCount, out int keysymsPerKeycode);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XQueryKeymap(nint display, byte[] keys);

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XFree(nint data);

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

    // ydotool types by evdev keycode through a fixed US keymap: anything outside
    // printable ASCII, Enter and Tab is dropped or garbled.
    public bool CanType(string text) =>
        text.All(c => c is >= ' ' and <= '~' or '\n' or '\t');

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
            ["Insert"] = (0xFF63, "110", "Insert"),  // KEY_INSERT = 110 (the Shift+Insert paste preset)
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
