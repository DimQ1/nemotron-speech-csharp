using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Windows text injector for the WinUI 3 head (net10.0-windows10.0.26100).
/// <see cref="Inject"/> types the text straight into the focused window with
/// SendInput Unicode key events (same as VoiceType.WinUI's SendInputStrategy):
/// the clipboard is neither used nor overwritten. With <see cref="Mode"/> set to
/// <see cref="TextInjectionMode.Paste"/> it puts the text on the clipboard and sends
/// <see cref="PasteChord"/> instead (for apps that ignore synthetic Unicode keys).
/// <see cref="CopyToClipboard"/> is the separate "Copy" action, via user32 without WinForms/WPF.
/// </summary>
public sealed class WindowsTextInjector : IPlatformTextInjector
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const ushort VK_RETURN = 0x0D;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_INSERT = 0x2D;
    private const ushort VK_V = 0x56;

    // Characters per SendInput call. Keeps a long transcript from flooding the
    // target's input queue in one go.
    private const int ChunkChars = 64;

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    /// <summary>Typed key by key (default) or pasted through the clipboard; set from the settings.</summary>
    public TextInjectionMode Mode { get; set; } = TextInjectionMode.Type;

    /// <summary>Shortcut sent in paste mode; set from the settings.</summary>
    public string PasteChord { get; set; } = "Ctrl+V";

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Paste mode: if the clipboard cannot be opened, typing still gets the text there.
        if (Mode == TextInjectionMode.Paste && CopyToClipboardCore(text))
        {
            var (modifiers, key) = ParseChord(PasteChord);
            SendChord(modifiers, key);
            return;
        }

        SendUnicodeText(text);
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        CopyToClipboardCore(text);
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = new List<INPUT>(ChunkChars * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                // A Unicode packet for '\n' is ignored by most edit controls: send
                // the real Enter / Tab keys, and treat "\r\n" as a single Enter.
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                        i++;
                    AddVkTap(inputs, VK_RETURN);
                    break;
                case '\n':
                    AddVkTap(inputs, VK_RETURN);
                    break;
                case '\t':
                    AddVkTap(inputs, VK_TAB);
                    break;
                default:
                    // Surrogate halves go out one by one; the system recombines them.
                    inputs.Add(MakeUnicodeKey(ch, keyUp: false));
                    inputs.Add(MakeUnicodeKey(ch, keyUp: true));
                    break;
            }

            if (inputs.Count >= ChunkChars * 2)
                Flush(inputs);
        }
        Flush(inputs);
    }

    private static void SendChord(ushort[] modifiers, ushort key)
    {
        var inputs = new List<INPUT>(modifiers.Length * 2 + 2);
        foreach (var modifier in modifiers)
            inputs.Add(MakeVkKey(modifier, keyUp: false));
        AddVkTap(inputs, key);
        foreach (var modifier in modifiers.Reverse())
            inputs.Add(MakeVkKey(modifier, keyUp: true));
        Flush(inputs);
    }

    /// <summary>
    /// "Ctrl+Shift+V" → modifier virtual keys + key. Understands the paste presets of the
    /// settings (Ctrl+V, Ctrl+Shift+V, Shift+Insert) and hand-edited chords with Ctrl/Shift/Alt/Win
    /// plus a letter, digit or Insert/Enter/Tab/Space; anything else falls back to Ctrl+V.
    /// </summary>
    internal static (ushort[] Modifiers, ushort Key) ParseChord(string? chord)
    {
        var modifiers = new List<ushort>();
        ushort key = 0;
        foreach (var part in (chord ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ushort modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => VK_CONTROL,
                "shift" => VK_SHIFT,
                "alt" => VK_MENU,
                "win" or "super" or "meta" => VK_LWIN,
                _ => 0,
            };
            if (modifier != 0)
            {
                if (!modifiers.Contains(modifier))
                    modifiers.Add(modifier);
                continue;
            }

            key = part.ToLowerInvariant() switch
            {
                "insert" or "ins" => VK_INSERT,
                "enter" or "return" => VK_RETURN,
                "tab" => VK_TAB,
                "space" => 0x20,
                [var c] when c is >= 'a' and <= 'z' => (ushort)char.ToUpperInvariant(c),
                [var c] when c is >= '0' and <= '9' => (ushort)c,
                _ => 0,
            };
            if (key == 0)
                return ([VK_CONTROL], VK_V);
        }

        return key == 0 ? ([VK_CONTROL], VK_V) : (modifiers.ToArray(), key);
    }

    private static void AddVkTap(List<INPUT> inputs, ushort vk)
    {
        inputs.Add(MakeVkKey(vk, keyUp: false));
        inputs.Add(MakeVkKey(vk, keyUp: true));
    }

    private static void Flush(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
            return;

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), InputSize);
        if (sent != inputs.Count)
        {
            // 0 usually means the foreground window is elevated (UIPI) or the desktop is locked.
            System.Diagnostics.Trace.WriteLine(
                $"[VoiceType.Uno] SendInput delivered {sent}/{inputs.Count} events (error {Marshal.GetLastWin32Error()})");
        }
        inputs.Clear();
    }

    private static INPUT MakeUnicodeKey(char ch, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = 0,
            wScan = ch,
            dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            time = 0,
            dwExtraInfo = nint.Zero
        }
    };

    private static INPUT MakeVkKey(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            // Insert/Delete/Win are "extended" keys: without the flag some apps read the numpad twin.
            dwFlags = (keyUp ? KEYEVENTF_KEYUP : 0) | (vk is VK_INSERT or 0x2E or VK_LWIN ? KEYEVENTF_EXTENDEDKEY : 0),
            time = 0,
            dwExtraInfo = nint.Zero
        }
    };

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private static bool CopyToClipboardCore(string text)
    {
        if (!OpenClipboard(nint.Zero))
            return false;
        try
        {
            EmptyClipboard();
            var bytes = (nint)((text.Length + 1) * 2);
            var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (hMem == nint.Zero)
                return false;
            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero)
                return false;
            try
            {
                var chars = (text + "\0").ToCharArray();
                Marshal.Copy(chars, 0, ptr, chars.Length);
            }
            finally { GlobalUnlock(hMem); }
            return SetClipboardData(CF_UNICODETEXT, hMem) != nint.Zero;
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
