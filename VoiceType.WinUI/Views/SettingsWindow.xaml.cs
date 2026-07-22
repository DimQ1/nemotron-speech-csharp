using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;
using Windows.Graphics;

namespace VoiceType.WinUI.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsViewModel ViewModel => _vm;
    public AppSettings ResultSettings { get; private set; } = null!;
    public bool WasSaved => _vm.WasSaved;

    public SettingsWindow(AppSettings currentSettings)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(currentSettings);
        _vm.OwnerWindowHandle = WindowNative.GetWindowHandle(this);

        // Compact size — resizable
        if (AppWindow is not null)
        {
            AppWindow.Resize(new SizeInt32(480, 480));
        }

        // Use standard system title bar
        ExtendsContentIntoTitleBar = false;

        _vm.RequestClose += () =>
        {
            ResultSettings = _vm.BuildSettings();
            this.Close();
        };
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PostProcessingRule rule })
            _vm.DeleteRuleCommand.Execute(rule);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
