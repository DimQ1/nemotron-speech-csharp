using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
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

        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FloatingText) && _vm is not null && _vm.IsAutoScrollEnabled)
        {
            // Auto-scroll to the latest text even when window doesn't have focus.
            // Use Loaded priority so layout has been updated before we scroll.
            Dispatcher.BeginInvoke(new Action(() => TextScroller.ScrollToBottom()),
                DispatcherPriority.Loaded);
        }
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
        GlobalHotkeyService.UnregisterAll();
    }

    private nint WndProcHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == GlobalHotkeyService.WmHotkey)
        {
            var hotkeyId = wParam.ToInt32();
            handled = _vm?.HandleHotkey(hotkeyId) ?? false;
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

    private void TextDisplay_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Context menu handled in XAML
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TextDisplay.SelectedText))
            TextDisplay.SelectAll();
        TextDisplay.Copy();
    }
}
