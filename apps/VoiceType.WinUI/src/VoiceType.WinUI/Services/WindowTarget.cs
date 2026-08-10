namespace VoiceType.WinUI.Services;

/// <summary>One remembered injection target: a top-level window and optional focused field.</summary>
public sealed record WindowTarget(
    string ProcessName,
    string WindowTitle,
    string? AutomationId,
    string? ControlType,
    string? ElementName,
    DateTimeOffset LastUsed,
    int UseCount)
{
    public string DisplayLabel
    {
        get
        {
            var fieldName = ElementName ?? AutomationId ?? ControlType;
            return string.IsNullOrWhiteSpace(fieldName)
                ? $"{ProcessName} - {WindowTitle}"
                : $"{ProcessName} - {fieldName}";
        }
    }
}

/// <summary>Snapshot of a window and its focused input element.</summary>
public sealed record WindowTargetInfo(
    nint Hwnd,
    string ProcessName,
    string WindowTitle,
    string? AutomationId,
    string? ControlType,
    string? ElementName)
{
    public string DisplayLabel
    {
        get
        {
            var fieldName = ElementName ?? AutomationId ?? ControlType;
            return string.IsNullOrWhiteSpace(fieldName)
                ? $"{ProcessName} - {WindowTitle}"
                : $"{ProcessName} - {fieldName}";
        }
    }
}