namespace VoiceType.WinUI.Interfaces;

public interface IGlobalHotkeyService
{
    int WmHotkey { get; }
    bool IsRegistered { get; }
    int Register(nint hwnd, string hotkeyString);
    void Unregister(int id);
    void UnregisterAll();
    bool TryParse(string hotkey, out uint modifiers, out uint vk);
}
