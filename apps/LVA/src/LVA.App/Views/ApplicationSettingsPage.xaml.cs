using LVA.App.Services;
using LVA.Plugins.Host;

namespace LVA.App.Views;

public partial class ApplicationSettingsPage : ContentPage
{
    private readonly IWindowManager _windowManager;
    private readonly PluginHost _plugins;

    public ApplicationSettingsPage(IWindowManager windowManager, PluginHost plugins)
    {
        InitializeComponent();
        _windowManager = windowManager;
        _plugins = plugins;
        SoundSwitch.IsToggled = Preferences.Get("lva.app.sound", true);
        HintsSwitch.IsToggled = Preferences.Get("lva.app.hints", true);
        AddPluginButtons();
    }

    private void AddPluginButtons()
    {
        foreach (var loaded in _plugins.Plugins.Where(p => p.Plugin.Manifest.Settings is not null))
        {
            var manifest = loaded.Plugin.Manifest;
            PluginList.Add(new Button
            {
                Text = $"{manifest.Settings!.Title}  (v{manifest.Version})",
                BackgroundColor = (Color)Application.Current!.Resources["Panel2Color"],
                TextColor = (Color)Application.Current.Resources["TextColor"],
                Command = new Command(() => _windowManager.OpenPluginSettingsWindow(manifest.Id))
            });
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        Preferences.Set("lva.app.sound", SoundSwitch.IsToggled);
        Preferences.Set("lva.app.hints", HintsSwitch.IsToggled);
        await DisplayAlertAsync("Настройки", "Настройки приложения сохранены.", "ОК");
    }
}