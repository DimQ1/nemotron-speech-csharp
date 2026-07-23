using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services.TextInjection;

namespace VoiceType.WinUI.Services;

public sealed class TextInjector : ITextInjector
{
    private readonly ITextInjectionStrategy _sendInput;
    private readonly ITextInjectionStrategy _clipboard;

    public TextInjector()
    {
        _sendInput = new SendInputStrategy();
        _clipboard = new ClipboardStrategy();
    }

    public void Inject(string text, InjectionMethod method)
    {
        if (string.IsNullOrEmpty(text)) return;

        var strategy = method switch
        {
            InjectionMethod.Clipboard => _clipboard,
            _ => _sendInput
        };
        strategy.Inject(text);
    }

    public void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        ClipboardSetText(text);
    }

    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

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
                var chars = (text + "\u0000").ToCharArray();
                Marshal.Copy(chars, 0, ptr, chars.Length);
            }
            finally { GlobalUnlock(hMem); }
            SetClipboardData(CF_UNICODETEXT, hMem);
        }
        finally { CloseClipboard(); }
    }
}