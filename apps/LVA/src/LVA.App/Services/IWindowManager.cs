namespace LVA.App.Services;

/// <summary>
/// Manages the separate chat and companion windows (TZ §14.2):
/// chat = normal window; companion = frameless always-on-top draggable overlay.
/// </summary>
public interface IWindowManager
{
    /// <summary>Opens (or focuses) the companion overlay window.</summary>
    void OpenCompanionWindow();

    /// <summary>Closes the companion overlay window.</summary>
    void CloseCompanionWindow();

    /// <summary>Opens (or focuses) the chat window.</summary>
    void OpenChatWindow();

    /// <summary>Opens the application's standalone settings window.</summary>
    void OpenApplicationSettingsWindow();

    /// <summary>Opens a standalone settings window for a registered plugin.</summary>
    void OpenPluginSettingsWindow(string pluginId);

    /// <summary>Whether the companion overlay is currently open.</summary>
    bool IsCompanionOpen { get; }

    /// <summary>Persisted companion position (desktop coordinates).</summary>
    Point CompanionPosition { get; set; }
}
