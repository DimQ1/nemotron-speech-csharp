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
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public KEYBDINPUT ki;
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

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

    /// <summary>
    /// Sends each character as a Unicode keystroke via <c>SendInput</c>.
    /// Driver-level simulation — works in any application, any language.
    /// </summary>
    private static void SendUnicodeText(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = new INPUT
            {
                type = 1, // INPUT_KEYBOARD
                ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE }
            };
            inputs[i * 2 + 1] = new INPUT
            {
                type = 1,
                ki = new KEYBDINPUT { wScan = text[i], dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP }
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Fallback: clipboard paste via Ctrl+V.
    /// Saves and restores clipboard content.
    /// </summary>
    private static void PasteViaClipboard(string text)
    {
        var saved = System.Windows.Clipboard.GetText();
        System.Windows.Clipboard.SetText(text);

        // Ctrl down + V down + V up + Ctrl up
        var inputs = new INPUT[4];
        inputs[0] = Key(0x11, 0);            // VK_CONTROL down
        inputs[1] = Key(0x56, 0);            // VK_V down
        inputs[2] = Key(0x56, KEYEVENTF_KEYUP); // VK_V up
        inputs[3] = Key(0x11, KEYEVENTF_KEYUP); // VK_CONTROL up
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());

        Task.Delay(200).ContinueWith(_ =>
        {
            try { System.Windows.Clipboard.SetText(saved); } catch { }
        }, TaskScheduler.Default);
    }

    private static INPUT Key(ushort vk, uint flags) => new()
    {
        type = 1,
        ki = new KEYBDINPUT { wVk = vk, dwFlags = flags }
    };
}

