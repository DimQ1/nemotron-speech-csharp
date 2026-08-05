using System.Runtime.InteropServices;
using VoiceType.Models;

namespace VoiceType.Services;

/// <summary>
/// Single point of control for voice-driven text injection in Voice Intent Mode:
/// enable/disable typing, retarget to a remembered window, and record targets.
/// Win32 focus switching is best-effort (AttachThreadInput + SetForegroundWindow);
/// when Windows refuses, the caller surfaces a "switch manually" status.
/// </summary>
public sealed class InjectionController
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int dwProcessId);
    [DllImport("user32.dll")] private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowTitle);
    [DllImport("user32.dll")] private static extern bool SetFocus(nint hWnd);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    private readonly WindowTargetHistory? _history;

    /// <summary>Raised after a successful injection, so the caller can record the target.</summary>
    public event Action<WindowTargetInfo>? Injected;

    public InjectionController(WindowTargetHistory? history)
    {
        _history = history;
    }

    /// <summary>Inject text and record the target in history (when enabled).</summary>
    public void Inject(string text, InjectionMethod method, bool recordTarget)
    {
        if (string.IsNullOrEmpty(text))
            return;

        TextInjector.Inject(text, method);

        if (recordTarget && _history is not null)
        {
            var snapshot = ForegroundWindowInspector.CaptureCurrent();
            if (!string.IsNullOrWhiteSpace(snapshot.ProcessName))
            {
                _history.Record(snapshot);
                Injected?.Invoke(snapshot);
            }
        }
    }

    /// <summary>
    /// Find a visible top-level window belonging to <paramref name="processName"/>
    /// whose title best matches <paramref name="windowTitleHint"/> (optional).
    /// Returns <see cref="nint.Zero"/> when nothing matches.
    /// </summary>
    public static nint FindWindowByProcess(string processName, string? windowTitleHint = null)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return nint.Zero;

        nint best = nint.Zero;
        int bestScore = -1;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            string proc;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                proc = p.ProcessName;
            }
            catch { return true; }

            if (!string.Equals(proc, processName, StringComparison.OrdinalIgnoreCase))
                return true;

            int score = 0;
            if (!string.IsNullOrWhiteSpace(windowTitleHint))
            {
                var info = ForegroundWindowInspector.Capture(hwnd);
                if (info.WindowTitle.Contains(windowTitleHint, StringComparison.OrdinalIgnoreCase))
                    score = 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
            return true;
        }, nint.Zero);

        return best;
    }

    /// <summary>
    /// Best-effort focus switch to <paramref name="hwnd"/> and (optionally) to a
    /// remembered input field inside it. Returns true when the window became
    /// foreground. Windows may legitimately refuse; caller handles false.
    /// </summary>
    public static bool TryFocusWindow(nint hwnd, WindowTarget? target = null)
    {
        if (hwnd == nint.Zero)
            return false;

        AllowSetForegroundWindow(-1); // ASFW_ANY

        var currentForeground = GetForegroundWindow();
        GetWindowThreadProcessId(currentForeground, out var foregroundThreadOwner);
        var foregroundThread = GetWindowThreadProcessId(currentForeground, out _);
        var currentThread = GetCurrentThreadId();

        var attached = false;
        if (foregroundThread != currentThread)
            attached = AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }

        var success = GetForegroundWindow() == hwnd;

        // Best-effort: focus the remembered input field by AutomationId via UIA.
        if (success && target?.AutomationId is { Length: > 0 } automationId)
            TryFocusField(hwnd, automationId);

        return success;
    }

    /// <summary>Best-effort UIA set-focus on a descendant element by AutomationId.</summary>
    private static void TryFocusField(nint hwnd, string automationId)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            if (root is null) return;

            var condition = new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.AutomationIdProperty, automationId);
            var element = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, condition);
            element?.SetFocus();
        }
        catch { /* UIA unavailable for this app — window-level focus is enough */ }
    }
}
