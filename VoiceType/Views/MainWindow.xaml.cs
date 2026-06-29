using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VoiceType.Services;
using VoiceType.ViewModels;

namespace VoiceType.Views;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = DataContext as MainViewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 60;

        // Register global hotkey
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);

        _vm?.RegisterHotkey(hwnd);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        GlobalHotkeyService.Unregister();
    }

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == GlobalHotkeyService.WmHotkey)
        {
            _vm?.Toggle();
            handled = true;
        }
        return nint.Zero;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
