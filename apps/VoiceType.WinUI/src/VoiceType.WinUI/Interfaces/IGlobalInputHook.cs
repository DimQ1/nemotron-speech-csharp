namespace VoiceType.WinUI.Interfaces;

public interface IGlobalInputHook : IDisposable
{
    event Action? InputDetected;
    void Install();
    void Uninstall();
}
