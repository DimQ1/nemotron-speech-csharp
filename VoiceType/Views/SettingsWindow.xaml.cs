using System.IO;
using System.Windows;
using VoiceType.Models;
using VoiceType.ViewModels;

namespace VoiceType.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public AppSettings ResultSettings { get; private set; } = null!;

    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(currentSettings);
        DataContext = _vm;

        _vm.RequestClose += () =>
        {
            ResultSettings = _vm.BuildSettings();
            DialogResult = _vm.WasSaved;
            Close();
        };
    }
}
