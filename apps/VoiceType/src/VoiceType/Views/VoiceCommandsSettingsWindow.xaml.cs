using System.Windows;
using VoiceType.Models;
using VoiceType.Services;
using VoiceType.ViewModels;

namespace VoiceType.Views;

/// <summary>Separate settings window for the optional Voice Commands (intent) mode.</summary>
public partial class VoiceCommandsSettingsWindow : Window
{
    private readonly VoiceCommandsSettingsViewModel _vm;
    private readonly VoiceIntentSettings _settings;

    public bool WasSaved { get; private set; }

    public VoiceCommandsSettingsWindow(VoiceIntentSettings settings, WindowTargetHistory? history)
    {
        InitializeComponent();
        _settings = settings;
        _vm = new VoiceCommandsSettingsViewModel(settings, history);
        DataContext = _vm;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.ApplyTo(_settings);
        WasSaved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        WasSaved = false;
        Close();
    }
}
