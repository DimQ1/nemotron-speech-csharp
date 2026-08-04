namespace VoiceType.WinUI.Services.TextInjection;

using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

/// <summary>Clipboard-based text injection (Ctrl+V paste fallback).</summary>
public sealed class ClipboardStrategy : ITextInjectionStrategy
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

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

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var saved = ClipboardGetText();
        ClipboardSetText(text);

        // Simulate Ctrl+V
        var inputs = new INPUT[4];
        inputs[0] = VkKey(0x11, 0);
        inputs[1] = VkKey(0x56, 0);
        inputs[2] = VkKey(0x56, KEYEVENTF_KEYUP);
        inputs[3] = VkKey(0x11, KEYEVENTF_KEYUP);
        SendInput(4, inputs, InputSize);

        // Restore clipboard after a delay
        Task.Delay(200).ContinueWith(_ =>
        {
            try { if (saved is not null) ClipboardSetText(saved); }
            catch { }
        }, TaskScheduler.Default);
    }

    private static INPUT VkKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags }
    };

    private static string? ClipboardGetText()
    {
        if (!OpenClipboard(nint.Zero)) return null;
        try
        {
            var hMem = GetClipboardData(CF_UNICODETEXT);
            if (hMem == nint.Zero) return null;
            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { GlobalUnlock(hMem); }
        }
        finally { CloseClipboard(); }
    }

    private static void ClipboardSetText(string text)
    {
        if (!OpenClipboard(nint.Zero)) return;
        try
        {
            EmptyClipboard();
            var bytes = (nint)((text.Length + 1) * 2);
            var hMem = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (hMem == nint.Zero) return;
            var ptr = GlobalLock(hMem);
            if (ptr == nint.Zero) return;
            try
            {
                var chars = (text + "\0").ToCharArray();
                Marshal.Copy(chars, 0, ptr, chars.Length);
            }
            finally { GlobalUnlock(hMem); }
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
        finally { CloseClipboard(); }
    }
}
