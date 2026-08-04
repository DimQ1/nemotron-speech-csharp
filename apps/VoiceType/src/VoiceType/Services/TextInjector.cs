using System.IO;
using System.Runtime.InteropServices;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Injects text into the currently focused input field via <c>SendInput</c> WINAPI.
/// Uses Unicode keystroke simulation — works with Cyrillic and any special characters.
/// Clipboard paste as fallback.
/// </summary>
public static class TextInjector
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    internal static readonly nint InjectionMarker = 0x565449;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ── Correct WINAPI struct layout (union via FieldOffset) ──

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

    // ── Public API ──────────────────────────────────

    /// <summary>Inject text into the currently focused window.</summary>
    public static void Inject(string text, InjectionMethod method)
    {
        if (string.IsNullOrEmpty(text)) return;

        switch (method)
        {
            case InjectionMethod.InputSimulator:
            case InjectionMethod.SendInput:
                SendUnicodeText(text);
                break;
            case InjectionMethod.Clipboard:
                PasteViaClipboard(text);
                break;
        }
    }

    // ── Unicode text entry (like InputSimulatorPlus.TextEntry) ──

    /// <summary>
    /// Sends each character as a Unicode keystroke via <c>SendInput</c>.
    /// Mimics InputSimulatorPlus behaviour: one character at a time,
    /// with <c>dwExtraInfo = GetMessageExtraInfo()</c>.
    /// </summary>
    private static void SendUnicodeText(string text)
    {
        var extraInfo = InjectionMarker;
        var inputs = new INPUT[2]; // keydown + keyup

        foreach (char c in text)
        {
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE, dwExtraInfo = extraInfo }
            };
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, dwExtraInfo = extraInfo }
            };
            SendInput(2, inputs, InputSize);
        }
    }

    // ── Clipboard paste fallback ────────────────────

    private static void PasteViaClipboard(string text)
    {
        var saved = System.Windows.Clipboard.GetText();
        System.Windows.Clipboard.SetText(text);

        var inputs = new INPUT[4];
        inputs[0] = VkKey(0x11, 0);
        inputs[1] = VkKey(0x56, 0);
        inputs[2] = VkKey(0x56, KEYEVENTF_KEYUP);
        inputs[3] = VkKey(0x11, KEYEVENTF_KEYUP);
        SendInput(4, inputs, InputSize);

        Task.Delay(200).ContinueWith(_ =>
        {
            try { System.Windows.Clipboard.SetText(saved); } catch { }
        }, TaskScheduler.Default);
    }

    private static INPUT VkKey(ushort vk, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = InjectionMarker }
    };
}

