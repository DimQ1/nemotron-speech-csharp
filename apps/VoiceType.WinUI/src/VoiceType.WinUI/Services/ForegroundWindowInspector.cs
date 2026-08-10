using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.WinUI.Services;

/// <summary>Captures foreground window and focused UI Automation metadata.</summary>
public static class ForegroundWindowInspector
{
    private const string CUIAutomationClassId = "FF48DBA4-60EF-4201-AA87-54103EEF594E";
    private const int UiAutomationIdProperty = 30011;
    private const int UiAutomationControlTypeProperty = 30003;
    private const int UiAutomationNameProperty = 30005;
    private const int TreeScopeDescendants = 4;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint windowHandle);

    public static WindowTargetInfo CaptureCurrent() => Capture(GetForegroundWindow());

    public static bool IsAlive(nint hwnd) => hwnd != nint.Zero && IsWindow(hwnd);

    public static WindowTargetInfo Capture(nint hwnd)
    {
        var processName = "";
        var title = "";
        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;

            var text = new StringBuilder(512);
            _ = GetWindowText(hwnd, text, text.Capacity);
            title = text.ToString();
        }
        catch
        {
            // The process may have exited or deny access while being inspected.
        }

        var focused = TryGetFocusedElement();
        return new WindowTargetInfo(
            hwnd,
            processName,
            title,
            focused.AutomationId,
            focused.ControlType,
            focused.Name);
    }

    public static bool TryFocusField(nint hwnd, string? automationId = null, string? controlType = null, string? name = null)
    {
        if (hwnd == nint.Zero)
            return false;

        object? automation = null;
        object? root = null;
        object? element = null;
        try
        {
            automation = CreateAutomationObject();
            if (automation is null)
                return false;

            dynamic uiAutomation = automation;
            root = uiAutomation.ElementFromHandle(hwnd);
            if (root is null)
                return false;

            dynamic rootElement = root;
            element = FindFieldElement(uiAutomation, rootElement, automationId, controlType, name);
            if (element is null)
                return false;

            dynamic focusedElement = element;
            focusedElement.SetFocus();
            return true;
        }
        catch
        {
            // UI Automation is unavailable for some elevated or custom-drawn apps.
            return false;
        }
        finally
        {
            ReleaseComObject(element);
            ReleaseComObject(root);
            ReleaseComObject(automation);
        }
    }

    private static object? FindFieldElement(dynamic uiAutomation, dynamic rootElement, string? automationId, string? controlType, string? name)
    {
        object? condition = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(automationId))
            {
                condition = uiAutomation.CreatePropertyCondition(UiAutomationIdProperty, automationId);
                if (condition is not null)
                {
                    var element = rootElement.FindFirst(TreeScopeDescendants, condition);
                    if (element is not null)
                        return element;
                }
            }

            foreach (var controlTypeId in GetControlTypeCandidates(controlType))
            {
                condition = uiAutomation.CreatePropertyCondition(UiAutomationControlTypeProperty, controlTypeId);
                if (condition is null)
                    continue;

                var element = rootElement.FindFirst(TreeScopeDescendants, condition);
                if (element is not null)
                    return element;
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                condition = uiAutomation.CreatePropertyCondition(UiAutomationNameProperty, name);
                if (condition is not null)
                {
                    var element = rootElement.FindFirst(TreeScopeDescendants, condition);
                    if (element is not null)
                        return element;
                }
            }

            return null;
        }
        finally
        {
            ReleaseComObject(condition);
        }
    }

    private static IEnumerable<int> GetControlTypeCandidates(string? controlType)
    {
        if (string.IsNullOrWhiteSpace(controlType))
            return new[] { 50004, 50030 };

        var normalized = controlType.Trim().ToLowerInvariant();
        var controlTypeId = normalized switch
        {
            "edit" => 50004,
            "document" => 50030,
            "textbox" => 50004,
            "text" => 50020,
            "combobox" => 50003,
            "list" => 50008,
            _ => 0,
        };

        return controlTypeId > 0
            ? new[] { controlTypeId, 50004, 50030 }
            : new[] { 50004, 50030 };
    }

    private static (string? AutomationId, string? ControlType, string? Name) TryGetFocusedElement()
    {
        object? automation = null;
        object? element = null;
        try
        {
            automation = CreateAutomationObject();
            if (automation is null)
                return (null, null, null);

            dynamic uiAutomation = automation;
            element = uiAutomation.GetFocusedElement();
            if (element is null)
                return (null, null, null);

            dynamic focusedElement = element;
            var automationId = ReadString(focusedElement.CurrentAutomationId);
            var name = ReadString(focusedElement.CurrentName);
            var controlType = ReadControlType(focusedElement.CurrentControlType, focusedElement.CurrentLocalizedControlType);
            return (automationId, controlType, name);
        }
        catch
        {
            return (null, null, null);
        }
        finally
        {
            ReleaseComObject(element);
            ReleaseComObject(automation);
        }
    }

    private static object? CreateAutomationObject()
    {
        try
        {
            var automationType = Type.GetTypeFromCLSID(new Guid(CUIAutomationClassId), throwOnError: true);
            return Activator.CreateInstance(automationType!);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ReadControlType(object? controlType, object? localizedControlType)
    {
        var localized = ReadString(localizedControlType);
        if (localized is not null)
            return localized;

        if (controlType is null)
            return null;

        return Convert.ToInt32(controlType, CultureInfo.InvariantCulture) switch
        {
            50002 => "Button",
            50003 => "ComboBox",
            50004 => "Edit",
            50007 => "Group",
            50008 => "List",
            50020 => "Text",
            50030 => "Document",
            _ => Convert.ToString(controlType, CultureInfo.InvariantCulture),
        };
    }

    private static void ReleaseComObject(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Release failures are non-fatal to the recognition pipeline.
        }
    }
}