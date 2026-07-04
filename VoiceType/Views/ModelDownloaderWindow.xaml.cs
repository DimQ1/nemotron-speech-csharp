using System.IO;
using System.Linq;
using System.Windows;
using VoiceType.ViewModels;

namespace VoiceType.Views;

public partial class ModelDownloaderWindow : Window
{
    private readonly ModelDownloaderViewModel _vm;

    public static ModelDownloaderWindow? FindOpenInstance() =>
        Application.Current.Windows
            .OfType<ModelDownloaderWindow>()
            .FirstOrDefault(window => window.IsLoaded);

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
        _vm = (ModelDownloaderViewModel)DataContext;
        Closed += (_, _) => _vm.Dispose();
    }
}
