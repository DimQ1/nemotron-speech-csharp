using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Windows text injector for the WinUI 3 head (net10.0-windows10.0.26100).
/// Mirrors VoiceType.WinUI TextInjector: SendInput typing for ASCII/Latin-1
/// via Unicode keyboard events, plus user32 clipboard without WinForms/WPF.
/// </summary>
public sealed class WindowsTextInjector : IPlatformTextInjector
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        // Clipboard + Ctrl+V is the most reliable path for arbitrary Unicode
        // text and large transcripts.
        if (!CopyToClipboardCore(text))
        {
            SendUnicodeText(text);
            return;
        }

        SendChord(VK_CONTROL, VK_V);
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        CopyToClipboardCore(text);
    }

    private static void SendUnicodeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(MakeUnicodeKey(ch, keyUp: false));
            inputs.Add(MakeUnicodeKey(ch, keyUp: true));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static void SendChord(ushort modifier, ushort key)
    {
        var inputs = new[]
        {
            MakeVkKey(modifier, keyUp: false),
            MakeVkKey(key, keyUp: false),
            MakeVkKey(key, keyUp: true),
            MakeVkKey(modifier, keyUp: true)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
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
            dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
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
