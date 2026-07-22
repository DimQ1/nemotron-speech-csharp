using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;
using Windows.Graphics;

namespace VoiceType.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private nint _hwnd;
    private SubclassProc? _subclassProc;
    private nint _subclassId = 1;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this.DispatcherQueue);
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        this.Activated += OnActivated;
        this.Closed += OnClosed;

        // Get HWND for hotkey registration
        _hwnd = WindowNative.GetWindowHandle(this);
        _vm.MainWindowHandle = _hwnd;

        // Compact window size (was 420x520 in WPF)
        if (AppWindow is not null)
        {
            AppWindow.Resize(new SizeInt32(420, 520));
        }

        // Use standard system title bar (no custom title bar)
        ExtendsContentIntoTitleBar = false;

        // Apply always-on-top from settings
        ApplyAlwaysOnTop(_vm.AlwaysOnTop);
        _vm.AlwaysOnTopChanged += ApplyAlwaysOnTop;

        // Register hotkey immediately — Activated may not fire if window starts active
        _vm.RegisterHotkey(_hwnd);
        _vm.TryAutoStart();
        SubclassWindow();

        // Debug log
        AppPaths.EnsureDataRoot();
        File.AppendAllText(AppPaths.ErrorLogFile, $"[{DateTime.Now}] MainWindow created: hwnd=0x{_hwnd:X}, hotkey registered\n");
    }

    public MainViewModel? ViewModel => _vm;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FloatingText) && _vm is not null && _vm.IsAutoScrollEnabled)
        {
            DispatcherQueue.TryEnqueue(() => TextScroller.ChangeView(null, double.MaxValue, null));
        }

        if (e.PropertyName == nameof(MainViewModel.IsRecording))
        {
            StatusDot.Fill = _vm?.IsRecording == true
                ? (Brush)Application.Current.Resources["RedBrush"]
                : (Brush)Application.Current.Resources["FgSecondaryBrush"];
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // Hotkey registration moved to constructor — Activated may not fire on first launch
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        GlobalHotkeyService.UnregisterAll();
        UnsubclassWindow();
    }

    private void SubclassWindow()
    {
        _subclassProc = WndProcHook;
        var ok = SetWindowSubclass(_hwnd, _subclassProc, _subclassId, nint.Zero);
        var err = Marshal.GetLastWin32Error();
        Console.WriteLine($"[VoiceType] SetWindowSubclass: hwnd=0x{_hwnd:X}, ok={ok}, error={err}");
    }

    private void UnsubclassWindow()
    {
        if (_subclassProc is not null)
            RemoveWindowSubclass(_hwnd, _subclassProc, _subclassId);
    }

    private nint WndProcHook(nint hwnd, uint msg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData)
    {
        if (msg == GlobalHotkeyService.WmHotkey)
        {
            var hotkeyId = wParam.ToInt32();
            Console.WriteLine($"[VoiceType] WM_HOTKEY received: id={hotkeyId}");
            AppPaths.EnsureDataRoot();
            File.AppendAllText(AppPaths.ErrorLogFile, $"[{DateTime.Now}] WM_HOTKEY: id={hotkeyId}\n");
            _vm?.HandleHotkey(hotkeyId);
            return nint.Zero;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        // Compact minimize — hide to taskbar
        this.AppWindow?.Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void DismissModelWarning_Click(object sender, RoutedEventArgs e)
    {
        _vm?.DismissModelWarning();
    }

    private void ApplyAlwaysOnTop(bool topmost)
    {
        if (AppWindow is null) return;
        // WinUI 3: use SetWindowPos via AppWindow
        var hwnd = WindowNative.GetWindowHandle(this);
        if (hwnd != nint.Zero)
        {
            const int HWND_TOPMOST = -1;
            const int HWND_NOTOPMOST = -2;
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            SetWindowPos(hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
