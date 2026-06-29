using System.IO;
using VoiceType.Models;
using WindowsInput;

namespace VoiceType.Services;

/// <summary>
/// Injects text into the currently focused input field.
/// Uses InputSimulatorPlus (SendInput driver-level) for reliable Unicode text entry,
/// with clipboard paste as a fallback.
/// </summary>
public static class TextInjector
{
    private static readonly InputSimulator Simulator = new();

    /// <summary>Inject text into the currently focused window.</summary>
    public static void Inject(string text, InjectionMethod method)
    {
        if (string.IsNullOrEmpty(text)) return;

        switch (method)
        {
            case InjectionMethod.InputSimulator:
            case InjectionMethod.SendInput:
                SendViaInputSimulator(text);
                break;
            case InjectionMethod.Clipboard:
                PasteViaClipboard(text);
                break;
        }
    }

    /// <summary>
    /// Uses InputSimulatorPlus <c>TextEntry</c> — simulates keystrokes at the driver level.
    /// Works with any characters including Cyrillic and special symbols.
    /// </summary>
    private static void SendViaInputSimulator(string text)
    {
        Simulator.Keyboard.TextEntry(text);
    }

    /// <summary>
    /// Fallback: clipboard paste via Ctrl+V.
    /// Saves and restores clipboard content.
    /// </summary>
    private static void PasteViaClipboard(string text)
    {
        var saved = System.Windows.Clipboard.GetText();
        System.Windows.Clipboard.SetText(text);

        Simulator.Keyboard.ModifiedKeyStroke(
            WindowsInput.Native.VirtualKeyCode.CONTROL,
            WindowsInput.Native.VirtualKeyCode.VK_V);

        Task.Delay(200).ContinueWith(_ =>
        {
            try { System.Windows.Clipboard.SetText(saved); } catch { /* ignore */ }
        }, TaskScheduler.Default);
    }
}

