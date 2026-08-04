using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Low-level global keyboard and mouse hook.
/// Fires <see cref="InputDetected"/> on ANY key press or mouse click.
/// </summary>
public sealed class GlobalInputHook : IGlobalInputHook
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int LLKHF_INJECTED = 0x10;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    private delegate nint LowLevelProc(int nCode, nint wParam, nint lParam);

    private readonly LowLevelProc _kbdProc;
    private readonly LowLevelProc _mouseProc;
    private nint _kbdHookId = nint.Zero;
    private nint _mouseHookId = nint.Zero;

    public event Action? InputDetected;

    public GlobalInputHook()
    {
        _kbdProc = KbdCallback;
        _mouseProc = MouseCallback;
    }

    public void Install()
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        var hMod = GetModuleHandle(curModule.ModuleName);
        _kbdHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        _mouseHookId = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    public void Uninstall()
    {
        if (_kbdHookId != nint.Zero) { UnhookWindowsHookEx(_kbdHookId); _kbdHookId = nint.Zero; }
        if (_mouseHookId != nint.Zero) { UnhookWindowsHookEx(_mouseHookId); _mouseHookId = nint.Zero; }
    }

    public void Dispose() => Uninstall();

    private nint KbdCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((data.flags & LLKHF_INJECTED) == 0 && data.dwExtraInfo != TextInjection.SendInputStrategy.InjectionMarker)
                InputDetected?.Invoke();
        }
        return CallNextHookEx(_kbdHookId, nCode, wParam, lParam);
    }

    private nint MouseCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
            InputDetected?.Invoke();
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public int flags;
        public uint time;
        public nint dwExtraInfo;
    }
}
