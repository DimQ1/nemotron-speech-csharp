namespace VoiceType.WinUI.Services.TextInjection;

using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

/// <summary>Unicode text injection via SendInput WINAPI (one char at a time).</summary>
public sealed class SendInputStrategy : ITextInjectionStrategy
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    internal static readonly nint InjectionMarker = 0x565449;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var extraInfo = InjectionMarker;
        var inputs = new INPUT[2];

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
}
