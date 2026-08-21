using Microsoft.UI.Xaml.Controls;
using VoiceType.Uno.Presentation;

namespace VoiceType.Uno;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }

    public MainViewModel ViewModel => (MainViewModel)DataContext;

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel.CreateSettingsSnapshot())
        {
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.ApplySettingsAsync(dialog.ViewModel.BuildSettings());
    }

    private async void Help_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HelpDialog { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }
}
