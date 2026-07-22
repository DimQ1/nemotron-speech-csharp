using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;
using Windows.Graphics;

namespace VoiceType.WinUI.Views;

public sealed partial class ModelDownloaderWindow : Window
{
    private readonly ModelDownloaderViewModel _vm;
    private static ModelDownloaderWindow? _openInstance;

    public static ModelDownloaderWindow? OpenInstance => _openInstance;

    public ModelDownloaderViewModel ViewModel => _vm;
    public string? ResultPath => _vm.ResultPath;
    public string? ResultModelPath => _vm.ResultModelPath;
    public bool WasDownloaded => _vm.WasDownloaded;

    /// <summary>Pre-fill the models root path for the downloader.</summary>
    public string ModelsRootPath
    {
        get => _vm.ModelsRootPath;
        set => _vm.ModelsRootPath = value;
    }

    public ModelDownloaderWindow()
    {
        InitializeComponent();
        _vm = new ModelDownloaderViewModel(this.DispatcherQueue);
        _vm.OwnerWindowHandle = WindowNative.GetWindowHandle(this);

        // Compact size
        if (AppWindow is not null)
        {
            AppWindow.Resize(new SizeInt32(480, 360));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }
        }

        // Custom title bar
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        _openInstance = this;
        this.Closed += (_, _) =>
        {
            _openInstance = null;
            _vm.Dispose();
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
