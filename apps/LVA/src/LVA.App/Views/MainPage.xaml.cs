using LVA.App.Services;
using LVA.App.ViewModels;

namespace LVA.App.Views;

public partial class MainPage : ContentPage
{
    private readonly IWindowManager? _windowManager;

    public MainPage(ConversationViewModel viewModel, IWindowManager? windowManager = null)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _windowManager = windowManager;
    }

    private void OnOpenChatClicked(object? sender, EventArgs e)
        => _windowManager?.OpenChatWindow();

    private void OnToggleCompanionClicked(object? sender, EventArgs e)
    {
        if (_windowManager is null)
            return;
        if (_windowManager.IsCompanionOpen)
            _windowManager.CloseCompanionWindow();
        else
            _windowManager.OpenCompanionWindow();
    }

    private void OnOpenSettingsClicked(object? sender, EventArgs e)
        => _windowManager?.OpenApplicationSettingsWindow();

    private void OnSendCompleted(object? sender, EventArgs e)
    {
        if (BindingContext is ConversationViewModel vm)
            _ = vm.SendCommand.ExecuteAsync(null);
    }
}
