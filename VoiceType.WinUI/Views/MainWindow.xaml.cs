using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private const int WM_HOTKEY = 0x0312;
    private nint _hwnd;
    private SubclassProc? _subclassProc;
    private nint _subclassId = 1;
    private readonly List<Window> _childWindows = new();
    private bool _isTopmostEnabled;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    public MainWindow(MainViewModel viewModel)
    {
        // ViewModel must be set BEFORE InitializeComponent for x:Bind to work
        _vm = viewModel;

        InitializeComponent();

        _vm.PropertyChanged += OnViewModelPropertyChanged;

        this.Activated += OnActivated;
        this.Closed += OnClosed;

        // Get HWND for hotkey registration
        _hwnd = WindowNative.GetWindowHandle(this);
        _vm.MainWindowHandle = _hwnd;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Apply always-on-top from settings
        _isTopmostEnabled = _vm.AlwaysOnTop;
        ApplyAlwaysOnTop(_isTopmostEnabled);
        _vm.AlwaysOnTopChanged += ApplyAlwaysOnTop;

        // Register hotkey immediately
        _vm.RegisterHotkey(_hwnd);
        _vm.TryAutoStart();
        SubclassWindow();
    }

    public void ConfigureWindow()
    {
        if (_hwnd != nint.Zero)
        {
            var dpi = GetWindowDpi(_hwnd);
            var w = (int)(372f * dpi / 96f);
            var h = (int)(600f * dpi / 96f);
            SetWindowPos(_hwnd, 0, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);
        }

        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }
    }

    public MainViewModel ViewModel => _vm;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FloatingText) && _vm.IsAutoScrollEnabled)
        {
            DispatcherQueue.TryEnqueue(() => TextScroller.ChangeView(null, double.MaxValue, null));
        }

        if (e.PropertyName == nameof(MainViewModel.IsRecording))
        {
            StatusDot.Fill = _vm.IsRecording
                ? (Brush)Application.Current.Resources["RedBrush"]
                : (Brush)Application.Current.Resources["FgSecondaryBrush"];
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args) { }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        var hotkeyService = App.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.UnregisterAll();
        UnsubclassWindow();

        // Close all child windows when main window closes
        foreach (var child in _childWindows.ToArray())
        {
            try { child.Close(); } catch { }
        }
        _childWindows.Clear();
    }

    private void SubclassWindow()
    {
        _subclassProc = WndProcHook;
        var ok = SetWindowSubclass(_hwnd, _subclassProc, _subclassId, nint.Zero);
        var err = Marshal.GetLastWin32Error();
        App.Telemetry?.LogInfo("Window", $"SetWindowSubclass: hwnd=0x{_hwnd:X}, ok={ok}, error={err}");
    }

    private void UnsubclassWindow()
    {
        if (_subclassProc is not null)
            RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
    }

    private nint WndProcHook(nint hwnd, uint msg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (msg == WM_HOTKEY)
        {
            var hotkeyId = wParam.ToInt32();
            App.Telemetry?.LogInfo("Window", $"WM_HOTKEY received: id={hotkeyId}");
            AppPaths.EnsureDataRoot();
            File.AppendAllText(AppPaths.ErrorLogFile, $"[{DateTime.Now}] WM_HOTKEY: id={hotkeyId}\n");
            _vm.HandleHotkey(hotkeyId);
            return nint.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        this.AppWindow?.Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void DismissModelWarning_Click(object sender, RoutedEventArgs e)
    {
        _vm.DismissModelWarning();
    }

    private void ApplyAlwaysOnTop(bool topmost)
    {
        _isTopmostEnabled = topmost;
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = topmost;
        }
    }

    /// <summary>Register a child window: positions it beside the main window (left/right based on screen space).</summary>
    public void TrackChildWindow(Window child)
    {
        if (child is null || _childWindows.Contains(child)) return;

        _childWindows.Add(child);

        child.Closed += (_, _) =>
        {
            _childWindows.Remove(child);
        };

        // Child windows must also be AlwaysOnTop so they appear beside the main window,
        // not behind it. The main window has AlwaysOnTop=true.
        if (child.AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }

        // Position after the window is fully rendered (Activated fires too early).
        child.Activated += (_, _) =>
        {
            child.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                PositionChildBeside(child);
            });
        };
    }

    /// <summary>Position child window to the left or right of the main window, whichever has more screen space.</summary>
    private void PositionChildBeside(Window child)
    {
        if (child is null || _hwnd == nint.Zero) return;

        var childHwnd = WindowNative.GetWindowHandle(child);
        if (childHwnd == nint.Zero) return;

        if (!GetWindowRect(_hwnd, out var mainRect)) return;
        if (!GetWindowRect(childHwnd, out var childRect)) return;

        var mainWidth = mainRect.Right - mainRect.Left;
        var mainHeight = mainRect.Bottom - mainRect.Top;
        var childWidth = childRect.Right - childRect.Left;
        var childHeight = childRect.Bottom - childRect.Top;

        var hmon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi)) return;

        var workArea = mi.rcWork;
        var spaceRight = workArea.Right - mainRect.Right;
        var spaceLeft = mainRect.Left - workArea.Left;

        int x;
        if (spaceRight >= childWidth + 8)
            x = mainRect.Right + 8;
        else if (spaceLeft >= childWidth + 8)
            x = mainRect.Left - childWidth - 8;
        else
            x = mainRect.Left; // fallback: overlap

        int y = mainRect.Top;

        // Clamp to work area
        if (x + childWidth > workArea.Right) x = workArea.Right - childWidth;
        if (x < workArea.Left) x = workArea.Left;
        if (y + childHeight > workArea.Bottom) y = workArea.Bottom - childHeight;
        if (y < workArea.Top) y = workArea.Top;

        SetWindowPos(childHwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ---- Win32 interop ----

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static int GetWindowDpi(nint hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        _ = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out _);
        return (int)dpiX;
    }
}