using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;

namespace VoiceType.WinUI.Services;

/// <summary>Win32 window operations (P/Invoke wrapper).</summary>
public sealed class WindowInterop : IWindowInterop
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetForegroundWindow")]
    private static extern nint GetForegroundWindowInternal();

    public nint GetForegroundWindow() => GetForegroundWindowInternal();
}