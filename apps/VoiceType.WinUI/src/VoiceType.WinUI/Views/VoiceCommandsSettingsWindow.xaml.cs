using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

public sealed partial class VoiceCommandsSettingsWindow : Window
{
    private const string WindowKey = "VoiceCommands";
    private static VoiceCommandsSettingsWindow? _openInstance;
    private readonly VoiceCommandsSettingsViewModel _viewModel;

    public static VoiceCommandsSettingsWindow? OpenInstance => _openInstance;
    public static bool TryAcquireGlobalGuard() => ChildWindowGuard.TryAcquire(WindowKey);
    public VoiceCommandsSettingsViewModel ViewModel => _viewModel;

    public VoiceCommandsSettingsWindow(AppSettings settings)
    {
        _viewModel = new VoiceCommandsSettingsViewModel(
            App.Services.GetRequiredService<ISettingsService>(),
            App.Services.GetRequiredService<VoiceCommandAppCatalog>(),
            settings);

        InitializeComponent();
        _viewModel.OwnerWindowHandle = WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyWindowSize();

        _openInstance = this;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_openInstance, this))
                _openInstance = null;
            ChildWindowGuard.Release(WindowKey);
        };
        _viewModel.RequestClose += Close;
    }

    private void ApplyWindowSize()
    {
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }

        AppWindow?.Resize(new Windows.Graphics.SizeInt32(620, 760));
    }

    private void RemoveApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VoiceCommandApp app })
            _viewModel.RemoveApplicationCommand.Execute(app);
    }
}