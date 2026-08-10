using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>Coordinates target-aware injection and best-effort foreground switching.</summary>
public sealed class InjectionController
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint windowHandle);

    private delegate bool EnumWindowsProc(nint windowHandle, nint lParam);

    private readonly ITextInjector _textInjector;
    private readonly IWindowInterop _windowInterop;
    private readonly WindowTargetHistory? _history;

    public event Action<WindowTargetInfo>? Injected;

    public InjectionController(
        ITextInjector textInjector,
        IWindowInterop windowInterop,
        WindowTargetHistory? history)
    {
        _textInjector = textInjector;
        _windowInterop = windowInterop;
        _history = history;
    }

    public void Inject(string text, InjectionMethod method, bool recordTarget)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _textInjector.Inject(text, method);

        if (!recordTarget || _history is null)
            return;

        var snapshot = ForegroundWindowInspector.Capture(_windowInterop.GetForegroundWindow());
        if (string.IsNullOrWhiteSpace(snapshot.ProcessName))
            return;

        _history.Record(snapshot);
        Injected?.Invoke(snapshot);
    }

    public static nint FindWindowByProcess(string processName, string? windowTitleHint = null)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return nint.Zero;

        nint best = nint.Zero;
        var bestScore = -1;
        EnumWindows((windowHandle, _) =>
        {
            if (!IsWindowVisible(windowHandle))
                return true;

            GetWindowThreadProcessId(windowHandle, out var processId);
            string currentProcessName;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                currentProcessName = process.ProcessName;
            }
            catch
            {
                return true;
            }

            if (!string.Equals(currentProcessName, processName, StringComparison.OrdinalIgnoreCase))
                return true;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(windowTitleHint))
            {
                var info = ForegroundWindowInspector.Capture(windowHandle);
                if (info.WindowTitle.Contains(windowTitleHint, StringComparison.OrdinalIgnoreCase))
                    score = 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = windowHandle;
            }

            return true;
        }, nint.Zero);

        return best;
    }

    public static bool TryFocusWindow(nint windowHandle, WindowTarget? target = null)
    {
        if (windowHandle == nint.Zero)
            return false;

        _ = AllowSetForegroundWindow(-1); // ASFW_ANY
        var currentForeground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(currentForeground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        if (foregroundThread != currentThread)
            attached = AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            _ = BringWindowToTop(windowHandle);
            _ = SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(currentThread, foregroundThread, false);
        }

        var success = GetForegroundWindow() == windowHandle;
        if (success)
            _ = ForegroundWindowInspector.TryFocusField(windowHandle, target?.AutomationId, target?.ControlType, target?.ElementName);

        return success;
    }
}