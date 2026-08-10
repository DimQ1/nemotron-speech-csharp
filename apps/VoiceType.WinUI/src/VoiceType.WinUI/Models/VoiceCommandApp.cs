namespace VoiceType.WinUI.Models;

/// <summary>Application available as a destination for a voice command.</summary>
public sealed class VoiceCommandApp
{
    public string DisplayName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public List<string> Aliases { get; set; } = new();

    public VoiceCommandApp Clone() => new()
    {
        DisplayName = DisplayName,
        ProcessName = ProcessName,
        ExecutablePath = ExecutablePath,
        Aliases = Aliases.ToList(),
    };
}