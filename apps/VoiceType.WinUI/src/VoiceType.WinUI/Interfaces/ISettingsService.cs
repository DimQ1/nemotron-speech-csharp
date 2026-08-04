namespace VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    void Update(Action<AppSettings> update);
    void SaveLanguage(string language);
}
