using LVA.App.Services;
using LVA.Plugins.Abstractions;

namespace LVA.App.Views;

public partial class PluginSettingsPage : ContentPage
{
    private readonly string _pluginId;
    private readonly PluginSettingsDefinition _definition;
    private readonly IPluginSettingsStore _settingsStore;
    private readonly Dictionary<PluginSettingField, Entry> _entries = new();

    public PluginSettingsPage(string pluginId, PluginSettingsDefinition definition, IPluginSettingsStore settingsStore)
    {
        InitializeComponent();
        _pluginId = pluginId;
        _definition = definition;
        _settingsStore = settingsStore;
        TitleLabel.Text = definition.Title;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        foreach (var field in _definition.Fields)
        {
            var entry = new Entry
            {
                Placeholder = field.Hint ?? field.Label,
                IsPassword = field.Secret,
                TextColor = (Color)Application.Current!.Resources["TextColor"],
                PlaceholderColor = (Color)Application.Current.Resources["Text2Color"],
                BackgroundColor = (Color)Application.Current.Resources["Panel2Color"]
            };
            entry.Text = await _settingsStore.GetAsync(_pluginId, field);
            _entries[field] = entry;
            Fields.Add(new VerticalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = field.Label, TextColor = (Color)Application.Current.Resources["TextColor"] },
                    entry
                }
            });
        }
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        foreach (var (field, entry) in _entries)
            await _settingsStore.SetAsync(_pluginId, field, entry.Text ?? string.Empty);

        await DisplayAlertAsync("Настройки", "Настройки плагина сохранены.", "ОК");
    }
}