using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

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

    public string ModelsRootPath
    {
        get => _vm.ModelsRootPath;
        set => _vm.ModelsRootPath = value;
    }

    public ModelDownloaderWindow()
    {
        // Resolve ViewModel from DI container
        _vm = App.Services.GetRequiredService<ModelDownloaderViewModel>();
        _vm.OwnerWindowHandle = WindowNative.GetWindowHandle(this);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowSize();

        _openInstance = this;
        this.Closed += (_, _) =>
        {
            _openInstance = null;
            _vm.Dispose();
        };
    }

    public void ApplyWindowSize()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetWindowDpi(hwnd);
        var w = (int)(600f * dpi / 96f);
        var h = (int)(372f * dpi / 96f);

        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, 0, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);

        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    // ---- Win32 interop ----

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static int GetWindowDpi(nint hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        _ = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out _);
        return (int)dpiX;
    }
}