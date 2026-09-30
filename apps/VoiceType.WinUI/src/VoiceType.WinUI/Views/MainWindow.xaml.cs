using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TaskbarService _taskbarService;
    private readonly WindowIconService _windowIconService;
    private const int WM_HOTKEY = 0x0312;
    private nint _hwnd;
    private SubclassProc? _subclassProc;
    private nint _subclassId = 1;
    private readonly List<Window> _childWindows = new();
    private readonly HashSet<Window> _childrenMinimizedWithMain = new();
    private bool _wasMinimized;
    private bool _isTopmostEnabled;
    private const double TextPaneMinHeight = 64;
    private const double TranslationDividerHeight = 18;
    private double _translationPaneRatio = 1d / 3d;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);

    /// <summary>Per-child-window subclass state: tracks whether the user is currently dragging/sizing the window.</summary>
    private sealed class ChildWindowState
    {
        public bool InSizeMove;
        public bool UserMoved;
        public bool IsMinimizing;
    }

    private readonly Dictionary<nint, (SubclassProc Proc, ChildWindowState State)> _childSubclass = new();

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
        _taskbarService = App.Services.GetRequiredService<TaskbarService>();
        _windowIconService = App.Services.GetRequiredService<WindowIconService>();

        InitializeComponent();
        TextAreaGrid.Loaded += (_, _) => UpdateTranslationLayout();

        _vm.PropertyChanged += OnViewModelPropertyChanged;

        this.Closed += OnClosed;

        // Get HWND for hotkey registration
        _hwnd = WindowNative.GetWindowHandle(this);
        _vm.MainWindowHandle = _hwnd;
        UpdateMicrophoneIcon();
        UpdateStatusBadge();

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

        // Initialize taskbar indicator after HWND is known
        _taskbarService.Initialize(_hwnd);
        if (_vm.IsRecording)
            _taskbarService.StartRecordingIndicator(
                _vm.IsCaptureMuted,
                ResolveTaskbarOverlayMode());
    }

    public void ConfigureWindow()
    {
        if (_hwnd != nint.Zero)
        {
            var dpi = GetWindowDpi(_hwnd);
            // Leave enough room for the app controls before the system caption buttons.
            var w = (int)(500f * dpi / 96f);
            var h = (int)(600f * dpi / 96f);
            SetWindowPos(_hwnd, 0, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);
        }

        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }
    }

    public MainViewModel ViewModel => _vm;

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FloatingText) && _vm.IsAutoScrollEnabled)
        {
            DispatcherQueue.TryEnqueue(() => TextScroller.ChangeView(null, double.MaxValue, null));
        }

        if (e.PropertyName == nameof(MainViewModel.TranslatedText) && _vm.IsAutoScrollEnabled)
        {
            DispatcherQueue.TryEnqueue(() => TranslationScroller.ChangeView(null, double.MaxValue, null));
        }

        if (e.PropertyName == nameof(MainViewModel.ShowTranslation))
            UpdateTranslationLayout();

        if (e.PropertyName is nameof(MainViewModel.IsRecording)
            or nameof(MainViewModel.IsActivelyInjecting)
            or nameof(MainViewModel.IsTextInjectionEnabled))
        {
            UpdateMicrophoneIcon();
            UpdateStatusBadge();
            UpdateTaskbarIndicator();
        }

        if (e.PropertyName == nameof(MainViewModel.IsCaptureMuted))
            UpdateTaskbarIndicator();
    }

    private void UpdateTranslationLayout()
    {
        if (!_vm.ShowTranslation)
        {
            RecognitionTextRow.Height = new GridLength(1, GridUnitType.Star);
            TranslationDividerRow.Height = new GridLength(0);
            TranslationTextRow.Height = new GridLength(0);
            return;
        }

        var translationRatio = Math.Clamp(_translationPaneRatio, 0.05, 0.95);
        RecognitionTextRow.Height = new GridLength(1 - translationRatio, GridUnitType.Star);
        TranslationDividerRow.Height = new GridLength(TranslationDividerHeight);
        TranslationTextRow.Height = new GridLength(translationRatio, GridUnitType.Star);
    }

    private void TranslationSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_vm.ShowTranslation)
            return;

        var availableHeight = TextAreaGrid.ActualHeight - TranslationDividerRow.ActualHeight;
        if (availableHeight <= TextPaneMinHeight * 2)
            return;

        var recognitionHeight = Math.Clamp(
            RecognitionTextRow.ActualHeight + e.VerticalChange,
            TextPaneMinHeight,
            availableHeight - TextPaneMinHeight);
        var translationHeight = availableHeight - recognitionHeight;

        _translationPaneRatio = translationHeight / availableHeight;
        RecognitionTextRow.Height = new GridLength(recognitionHeight / availableHeight, GridUnitType.Star);
        TranslationTextRow.Height = new GridLength(translationHeight / availableHeight, GridUnitType.Star);
    }

    private void UpdateMicrophoneIcon()
    {
        var isTextInjectionActive = _vm.IsRecording && _vm.IsTextInjectionEnabled;
        MicrophoneIcon.Foreground = isTextInjectionActive
            ? (Brush)Application.Current.Resources["RedBrush"]
            : (Brush)Application.Current.Resources["AccentBrush"];
        _windowIconService.SetWindowIcon(_hwnd, AppWindow, isTextInjectionActive);
    }

    private void UpdateStatusBadge()
    {
        if (!_vm.IsRecording)
        {
            StatusDot.Visibility = Visibility.Collapsed;
            StatusInjectionBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var showInjectionBadge = _vm.IsActivelyInjecting;
        StatusInjectionBadge.Visibility = showInjectionBadge ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Visibility = showInjectionBadge ? Visibility.Collapsed : Visibility.Visible;
    }

    private TaskbarService.RecordingOverlayMode ResolveTaskbarOverlayMode()
        => _vm.IsActivelyInjecting
            ? TaskbarService.RecordingOverlayMode.InjectionText
            : TaskbarService.RecordingOverlayMode.CaptureDot;

    private void UpdateTaskbarIndicator()
    {
        var isRecording = _vm.IsRecording;
        var isCaptureMuted = _vm.IsCaptureMuted;
        var overlayMode = ResolveTaskbarOverlayMode();

        DispatcherQueue.TryEnqueue(() =>
        {
            if (isRecording)
                _taskbarService.StartRecordingIndicator(isCaptureMuted, overlayMode);
            else
                _taskbarService.StopRecordingIndicator();
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _taskbarService.StopRecordingIndicator();
        _taskbarService.Dispose();

        var hotkeyService = App.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.UnregisterAll();
        UnsubclassWindow();

        // Close all child windows when main window closes
        foreach (var child in _childWindows.ToArray())
        {
            try { child.Close(); } catch { }
        }
        _childWindows.Clear();
        _childrenMinimizedWithMain.Clear();
        _windowIconService.Dispose();
    }

    private void SynchronizeChildWindowState(bool isMinimized)
    {
        if (isMinimized == _wasMinimized)
            return;

        _wasMinimized = isMinimized;
        if (isMinimized)
            MinimizeChildWindows();
        else
            RestoreChildWindows();
    }

    private void MinimizeChildWindows()
    {
        _childrenMinimizedWithMain.Clear();

        foreach (var child in _childWindows.ToArray())
        {
            if (child.AppWindow is not { IsVisible: true, Presenter: OverlappedPresenter presenter }
                || presenter.State == OverlappedPresenterState.Minimized)
            {
                continue;
            }

            _childrenMinimizedWithMain.Add(child);
            presenter.Minimize();
        }
    }

    private void RestoreChildWindows()
    {
        foreach (var child in _childrenMinimizedWithMain.ToArray())
        {
            if (_childWindows.Contains(child)
                && child.AppWindow?.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore(false);
            }
        }

        _childrenMinimizedWithMain.Clear();
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
        const uint WM_SIZE = 0x0005;
        const long SIZE_MINIMIZED = 1;

        if (msg == WM_HOTKEY)
        {
            var hotkeyId = wParam.ToInt32();
            App.Telemetry?.LogInfo("Window", $"WM_HOTKEY received: id={hotkeyId}");
            AppPaths.EnsureDataRoot();
            // Debug-only hotkey logging removed — was causing file-lock storms
            _vm.HandleHotkey(hotkeyId);
            return nint.Zero;
        }

        if (msg == WM_SIZE)
        {
            var isMinimized = (wParam.ToInt64() & 0xFFFF) == SIZE_MINIMIZED;
            DispatcherQueue.TryEnqueue(() => SynchronizeChildWindowState(isMinimized));
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow?.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
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

    /// <summary>
    /// Registers a child window: keeps its icon and z-order in sync with the main window,
    /// mirrors the main window's minimized state and stops Windows from re-arranging it on
    /// activation. Child windows are not positioned by the app.
    /// </summary>
    public void TrackChildWindow(Window child)
    {
        if (child is null || _childWindows.Contains(child)) return;

        _childWindows.Add(child);

        var childHwnd = WindowNative.GetWindowHandle(child);
        var state = new ChildWindowState();
        _windowIconService.SetWindowIcon(childHwnd, child.AppWindow, isTextInjectionActive: false);

        // Subclass the child window to veto moves that are NOT initiated by the user
        // (Windows Snap Assist / DWM re-arrangement on activation change), while
        // allowing genuine user drag/resize (tracked via WM_ENTERSIZEMOVE/EXITSIZEMOVE).
        if (childHwnd != nint.Zero)
        {
            SubclassProc proc = (hwnd, msg, wParam, lParam, uIdSubclass, dwRefData) =>
            {
                const uint WM_SIZE = 0x0005;
                const uint WM_SYSCOMMAND = 0x0112;
                const uint WM_ENTERSIZEMOVE = 0x0231;
                const uint WM_EXITSIZEMOVE = 0x0232;
                const uint WM_WINDOWPOSCHANGING = 0x0046;
                const long SC_MINIMIZE = 0xF020;
                const long SC_RESTORE = 0xF120;
                const long SIZE_MINIMIZED = 1;
                const uint SWP_NOMOVE_FLAG = 0x0002;

                if (msg == WM_SYSCOMMAND)
                {
                    var command = wParam.ToInt64() & 0xFFF0;
                    state.IsMinimizing = command == SC_MINIMIZE;
                    if (command == SC_RESTORE)
                    {
                        state.IsMinimizing = false;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (child.AppWindow?.Presenter is OverlappedPresenter presenter
                                && presenter.State == OverlappedPresenterState.Minimized)
                            {
                                presenter.Restore(false);
                            }
                        });
                        return nint.Zero;
                    }
                }
                else if (msg == WM_SIZE)
                {
                    state.IsMinimizing = (wParam.ToInt64() & 0xFFFF) == SIZE_MINIMIZED;
                }
                else if (msg == WM_ENTERSIZEMOVE)
                    state.InSizeMove = true;
                else if (msg == WM_EXITSIZEMOVE)
                {
                    if (state.InSizeMove)
                        state.UserMoved = true;
                    state.InSizeMove = false;
                }
                else if (msg == WM_WINDOWPOSCHANGING
                    && !state.InSizeMove
                    && !state.IsMinimizing
                    && !IsIconic(hwnd))
                {
                    // A move request that did not come from user drag/resize — strip the
                    // position change so the window stays where the user left it.
                    var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    if ((pos.flags & SWP_NOMOVE_FLAG) == 0)
                    {
                        pos.flags |= SWP_NOMOVE_FLAG;
                        Marshal.StructureToPtr(pos, lParam, false);
                    }
                }
                return DefSubclassProc(hwnd, msg, wParam, lParam);
            };

            var subclassId = (nint)(childHwnd.ToInt64() ^ 0x5A5A);
            if (SetWindowSubclass(childHwnd, proc, subclassId, nint.Zero))
                _childSubclass[childHwnd] = (proc, state);
        }

        child.Closed += (_, _) =>
        {
            _childWindows.Remove(child);
            _childrenMinimizedWithMain.Remove(child);
            if (childHwnd != nint.Zero && _childSubclass.Remove(childHwnd, out var entry))
                RemoveWindowSubclass(childHwnd, entry.Proc, (nint)(childHwnd.ToInt64() ^ 0x5A5A));
        };

        // Child windows are kept AlwaysOnTop so they stay visible above the main window,
        // which is also AlwaysOnTop. Their position is left to Windows (normal cascade).
        if (child.AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
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

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public nint hwnd;
        public nint hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    private static int GetWindowDpi(nint hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        _ = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out _);
        return (int)dpiX;
    }
}