using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private nint _hwnd;
    private WndProcDelegate? _newWndProc;
    private nint _oldWndProc;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    private const int GWLP_WNDPROC = -4;

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
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            // Register global hotkey
            _vm?.RegisterHotkey(_hwnd);

            // Auto-start recognition if enabled in settings
            _vm?.TryAutoStart();

            // Subclass window to hook WM_HOTKEY
            SubclassWindow();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        GlobalHotkeyService.UnregisterAll();
        UnsubclassWindow();
    }

    private void SubclassWindow()
    {
        _newWndProc = WndProcHook;
        _oldWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_newWndProc));
    }

    private void UnsubclassWindow()
    {
        if (_oldWndProc != nint.Zero)
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
    }

    private nint WndProcHook(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == GlobalHotkeyService.WmHotkey)
        {
            var hotkeyId = wParam.ToInt32();
            _vm?.HandleHotkey(hotkeyId);
            return nint.Zero;
        }
        return CallWindowProc(_oldWndProc, hwnd, msg, wParam, lParam);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        // WinUI 3: hide window via AppWindow
        this.AppWindow?.Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}
