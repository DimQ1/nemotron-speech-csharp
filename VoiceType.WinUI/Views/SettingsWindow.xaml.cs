using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsViewModel ViewModel => _vm;
    public AppSettings ResultSettings { get; private set; } = null!;
    public bool WasSaved => _vm.WasSaved;

    public SettingsWindow(AppSettings currentSettings)
    {
        // Resolve ISettingsService from DI container
        var settingsService = App.Services.GetRequiredService<ISettingsService>();
        _vm = new SettingsViewModel(settingsService, currentSettings);
        _vm.OwnerWindowHandle = WindowNative.GetWindowHandle(this);

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowSize();

        _vm.RequestClose += () =>
        {
            ResultSettings = _vm.BuildSettings();
            this.Close();
        };
    }

    public void ApplyWindowSize()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetWindowDpi(hwnd);
        var w = (int)(460f * dpi / 96f);
        var h = (int)(640f * dpi / 96f);

        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, 0, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);

        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }
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