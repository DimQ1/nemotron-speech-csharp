using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Injects text into the target window using either <c>SendInput</c> (Unicode)
/// or clipboard paste (<c>Ctrl+V</c>).
/// </summary>
public static class TextInjector
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern nint GetMessageExtraInfo();

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
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

    /// <summary>Inject text into the currently focused window.</summary>
    public static void Inject(string text, InjectionMethod method)
    {
        if (string.IsNullOrEmpty(text)) return;

        switch (method)
        {
            case InjectionMethod.SendInput:
                SendUnicode(text);
                break;
            case InjectionMethod.Clipboard:
                PasteViaClipboard(text);
                break;
        }
    }

    private static void SendUnicode(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            // Key down
            inputs[i * 2] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = text[i],
                        dwFlags = KEYEVENTF_UNICODE,
                        dwExtraInfo = GetMessageExtraInfo()
                    }
                }
            };
            // Key up
            inputs[i * 2 + 1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = text[i],
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        dwExtraInfo = GetMessageExtraInfo()
                    }
                }
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void PasteViaClipboard(string text)
    {
        // Save clipboard
        var saved = System.Windows.Clipboard.GetText();

        System.Windows.Clipboard.SetText(text);

        // Simulate Ctrl+V
        var inputs = new INPUT[4];
        inputs[0] = KeyDown(VK_CONTROL);
        inputs[1] = KeyDown(VK_V);
        inputs[2] = KeyUp(VK_V);
        inputs[3] = KeyUp(VK_CONTROL);
        SendInput(4, inputs, Marshal.SizeOf<INPUT>());

        // Restore clipboard after a short delay
        Task.Delay(200).ContinueWith(_ =>
        {
            try { System.Windows.Clipboard.SetText(saved); } catch { /* ignore */ }
        }, TaskScheduler.Default);
    }

    private static INPUT KeyDown(byte vk) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } }
    };

    private static INPUT KeyUp(byte vk) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
    };
}
