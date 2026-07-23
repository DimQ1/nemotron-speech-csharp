namespace VoiceType.WinUI.Interfaces;

/// <summary>Abstracts Win32 window operations for testability and MVVM purity.</summary>
public interface IWindowInterop
{
    /// <summary>Get the handle of the foreground window.</summary>
    nint GetForegroundWindow();
}