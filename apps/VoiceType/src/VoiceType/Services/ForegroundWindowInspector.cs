using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.Services;

/// <summary>Snapshot of a window and its focused input element.</summary>
public sealed record WindowTargetInfo(
    nint Hwnd,
    string ProcessName,
    string WindowTitle,
    string? AutomationId,
    string? ControlType,
    string? ElementName)
{
    /// <summary>Short human-readable label for clarify dialogs and history views.</summary>
    public string DisplayLabel
    {
        get
        {
            var fieldName = ElementName ?? AutomationId ?? ControlType;
            return string.IsNullOrWhiteSpace(fieldName)
                ? $"{ProcessName} — {WindowTitle}"
                : $"{ProcessName} — {fieldName}";
        }
    }
}

/// <summary>
/// Inspects the foreground window via Win32 and (best-effort) UI Automation.
/// All UIA access is guarded — when UIA is unavailable (elevated apps, games)
/// the snapshot simply has no AutomationId/ControlType and callers fall back
/// to window-level targeting.
/// </summary>
public static class ForegroundWindowInspector
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);

    /// <summary>Capture the current foreground window + focused element.</summary>
    public static WindowTargetInfo CaptureCurrent() => Capture(GetForegroundWindow());

    /// <summary>True when the window handle still refers to a live window.</summary>
    public static bool IsAlive(nint hwnd) => hwnd != nint.Zero && IsWindow(hwnd);

    /// <summary>Capture metadata for a specific window handle.</summary>
    public static WindowTargetInfo Capture(nint hwnd)
    {
        string processName = "";
        string title = "";
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;

            var sb = new StringBuilder(512);
            _ = GetWindowText(hwnd, sb, sb.Capacity);
            title = sb.ToString();
        }
        catch { /* process exited / access denied */ }

        var (autoId, controlType, elementName) = TryGetFocusedElement();
        return new WindowTargetInfo(hwnd, processName, title, autoId, controlType, elementName);
    }

    /// <summary>Best-effort UIA focused-element capture. Never throws.</summary>
    private static (string? AutomationId, string? ControlType, string? Name) TryGetFocusedElement()
    {
        try
        {
            // System.Windows.Automation (UIAutomationClient) — available in WPF.
            var element = System.Windows.Automation.AutomationElement.FocusedElement;
            if (element is null) return (null, null, null);

            var autoId = element.Current.AutomationId;
            var controlType = element.Current.ControlType.ProgrammaticName;
            var name = element.Current.Name;
            return (
                string.IsNullOrWhiteSpace(autoId) ? null : autoId,
                string.IsNullOrWhiteSpace(controlType) ? null : controlType.Replace("ControlType.", ""),
                string.IsNullOrWhiteSpace(name) ? null : name);
        }
        catch
        {
            return (null, null, null);
        }
    }
}
