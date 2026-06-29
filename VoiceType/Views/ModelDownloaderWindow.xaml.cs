using System.IO;
using System.Windows;
using VoiceType.ViewModels;

namespace VoiceType.Views;

public partial class ModelDownloaderWindow : Window
{
    private readonly ModelDownloaderViewModel _vm;

    public string? ResultPath => _vm.ResultPath;
    public bool WasDownloaded => _vm.WasDownloaded;

    public ModelDownloaderWindow()
    {
        InitializeComponent();
        _vm = (ModelDownloaderViewModel)DataContext;
        Closed += (_, _) => _vm.Dispose();
    }
}
