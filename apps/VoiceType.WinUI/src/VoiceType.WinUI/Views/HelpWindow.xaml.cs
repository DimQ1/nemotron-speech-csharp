using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

/// <summary>
/// Static "How to use VoiceType" help window. Opened from the title-bar help button
/// and shown automatically at the end of the first-run wizard.
/// </summary>
public sealed partial class HelpWindow : Window
{
    private static HelpWindow? _openInstance;

    /// <summary>Currently open help window, or null when closed. Prevents duplicates.</summary>
    public static HelpWindow? OpenInstance => _openInstance;

    public HelpWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowSize();

        _openInstance = this;
        this.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openInstance, this))
                _openInstance = null;
        };
    }

    /// <summary>Show the help window (or focus the existing one).</summary>
    public static void Show()
    {
        if (OpenInstance is { } existing)
        {
            existing.RestoreAndActivate();
            return;
        }
        var window = new HelpWindow();
        App.MainWindow?.TrackChildWindow(window);
        window.Activate();
    }

    private void RestoreAndActivate()
    {
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore(true);
            return;
        }

        Activate();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => this.Close();

    // ---- Window sizing ----

    private void ApplyWindowSize()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetWindowDpi(hwnd);
        var w = (int)(520f * dpi / 96f);
        var h = (int)(640f * dpi / 96f);

        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, 0, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);

        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
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
