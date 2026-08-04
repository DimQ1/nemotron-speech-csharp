using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>Win32 window operations (P/Invoke wrapper).</summary>
public sealed class WindowInterop : IWindowInterop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowInternal();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    public nint GetForegroundWindow() => GetForegroundWindowInternal();

    public nint GetOwnWindowHandle()
    {
        return App.MainWindow is not null
            ? WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)
            : nint.Zero;
    }

    public bool IsWindowInCurrentProcess(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
            return false;

        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId == (uint)Environment.ProcessId;
    }
}