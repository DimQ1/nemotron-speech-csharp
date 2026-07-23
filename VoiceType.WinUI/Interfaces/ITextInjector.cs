namespace VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

public interface ITextInjector
{
    void Inject(string text, InjectionMethod method);
    void CopyToClipboard(string text);
}
