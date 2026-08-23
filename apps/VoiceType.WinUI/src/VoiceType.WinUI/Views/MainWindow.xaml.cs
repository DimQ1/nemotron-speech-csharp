using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
    private readonly TaskbarService _taskbarService;
    private readonly WindowIconService _windowIconService;
    private const int WM_HOTKEY = 0x0312;
    private nint _hwnd;
    private SubclassProc? _subclassProc;
    private nint _subclassId = 1;
    private readonly List<Window> _childWindows = new();
    private readonly HashSet<nint> _initiallyPlacedChildWindows = new();
    private bool _isTopmostEnabled;
    private bool _childPlacementScheduled;
    private DispatcherQueueTimer? _childPlacementTimer;
    private int _childPlacementAttempts;
    private const int MaxInitialPlacementAttempts = 20;

    private delegate nint SubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam, nint uIdSubclass, nint dwRefData);
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    /// <summary>Per-child-window subclass state: tracks whether the user is currently dragging/sizing the window.</summary>
    private sealed class ChildWindowState
    {
        public bool InSizeMove;
        public bool AllowProgrammaticMove;
        public bool UserMoved;
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

        if (e.PropertyName == nameof(MainViewModel.TranslatedText) && _vm.IsAutoScrollEnabled)
        {
            DispatcherQueue.TryEnqueue(() => TranslationScroller.ChangeView(null, double.MaxValue, null));
        }

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
        _childPlacementTimer?.Stop();

        var hotkeyService = App.Services.GetRequiredService<IGlobalHotkeyService>();
        hotkeyService.UnregisterAll();
        UnsubclassWindow();

        // Close all child windows when main window closes
        foreach (var child in _childWindows.ToArray())
        {
            try { child.Close(); } catch { }
        }
        _childWindows.Clear();
        _initiallyPlacedChildWindows.Clear();
        _windowIconService.Dispose();
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
            // Debug-only hotkey logging removed — was causing file-lock storms
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
                const uint WM_ENTERSIZEMOVE = 0x0231;
                const uint WM_EXITSIZEMOVE = 0x0232;
                const uint WM_WINDOWPOSCHANGING = 0x0046;
                const uint SWP_NOMOVE_FLAG = 0x0002;

                if (msg == WM_ENTERSIZEMOVE)
                    state.InSizeMove = true;
                else if (msg == WM_EXITSIZEMOVE)
                {
                    if (state.InSizeMove)
                        state.UserMoved = true;
                    state.InSizeMove = false;
                }
                else if (msg == WM_WINDOWPOSCHANGING && !state.InSizeMove && !state.AllowProgrammaticMove)
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
            _initiallyPlacedChildWindows.Remove(childHwnd);
            if (childHwnd != nint.Zero && _childSubclass.Remove(childHwnd, out var entry))
                RemoveWindowSubclass(childHwnd, entry.Proc, (nint)(childHwnd.ToInt64() ^ 0x5A5A));
        };

        // Child windows must also be AlwaysOnTop so they appear beside the main window,
        // not behind it. The main window has AlwaysOnTop=true.
        if (child.AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
        }

        child.Activated += (_, _) => ScheduleChildPlacement();
        ScheduleChildPlacement();
    }

    private void ScheduleChildPlacement()
    {
        if (_childPlacementScheduled)
            return;

        _childPlacementScheduled = true;
        _childPlacementAttempts = 0;
        _childPlacementTimer = DispatcherQueue.CreateTimer();
        _childPlacementTimer.Interval = TimeSpan.FromMilliseconds(100);
        _childPlacementTimer.Tick += OnChildPlacementTimerTick;
        _childPlacementTimer.Start();
    }

    private void OnChildPlacementTimerTick(DispatcherQueueTimer sender, object args)
    {
        _childPlacementAttempts++;
        ArrangeInitialChildWindows();

        if (_childPlacementAttempts < MaxInitialPlacementAttempts && _childWindows.Count > 0)
            return;

        sender.Stop();
        sender.Tick -= OnChildPlacementTimerTick;
        _childPlacementTimer = null;
        _childPlacementScheduled = false;
    }

    private void ArrangeInitialChildWindows()
    {
        foreach (var child in _childWindows.ToArray())
        {
            var childHwnd = WindowNative.GetWindowHandle(child);
            if (childHwnd == nint.Zero)
                continue;

            if (_initiallyPlacedChildWindows.Contains(childHwnd))
                continue;

            if (_childSubclass.TryGetValue(childHwnd, out var entry)
                && (entry.State.UserMoved || entry.State.InSizeMove))
            {
                if (entry.State.UserMoved)
                    _initiallyPlacedChildWindows.Add(childHwnd);
                continue;
            }

            if (!IsWindowVisible(childHwnd))
                continue;

            if (PositionChildBeside(child))
                _initiallyPlacedChildWindows.Add(childHwnd);
        }
    }

    /// <summary>Position a child in the nearest free slot around the main window and other children.</summary>
    private bool PositionChildBeside(Window child)
    {
        if (child is null || _hwnd == nint.Zero) return false;

        var childHwnd = WindowNative.GetWindowHandle(child);
        if (childHwnd == nint.Zero) return false;

        if (!GetWindowRect(_hwnd, out var mainRect)) return false;
        if (!GetWindowRect(childHwnd, out var childRect)) return false;

        var mainWidth = mainRect.Right - mainRect.Left;
        var mainHeight = mainRect.Bottom - mainRect.Top;
        var childWidth = childRect.Right - childRect.Left;
        var childHeight = childRect.Bottom - childRect.Top;

        if (childWidth <= 0 || childHeight <= 0) return false;

        var occupied = new List<RECT> { mainRect };
        foreach (var trackedChild in _childWindows)
        {
            var trackedHwnd = WindowNative.GetWindowHandle(trackedChild);
            if (trackedHwnd != nint.Zero && trackedHwnd != childHwnd
                && GetWindowRect(trackedHwnd, out var otherRect))
                occupied.Add(otherRect);
        }

        var hmon = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hmon, ref mi)) return false;

        var workArea = mi.rcWork;
        if (!IsWindowVisible(_hwnd))
        {
            var centeredX = workArea.Left + Math.Max(0, (workArea.Right - workArea.Left - childWidth) / 2);
            var centeredY = workArea.Top + Math.Max(0, (workArea.Bottom - workArea.Top - childHeight) / 2);
            MoveChildWindow(childHwnd, centeredX, centeredY);
            return true;
        }

        var hasFreePosition = TryFindFreePosition(
            mainRect,
            childWidth,
            childHeight,
            workArea,
            occupied,
            out var x,
            out var y);

        if (!hasFreePosition)
        {
            x = Math.Clamp(mainRect.Right + WindowGap, workArea.Left, workArea.Right - childWidth);
            y = Math.Clamp(mainRect.Top, workArea.Top, workArea.Bottom - childHeight);
        }

        MoveChildWindow(childHwnd, x, y);
        return true;
    }

    private void MoveChildWindow(nint childHwnd, int x, int y)
    {
        if (_childSubclass.TryGetValue(childHwnd, out var entry))
            entry.State.AllowProgrammaticMove = true;

        try
        {
            SetWindowPos(childHwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        finally
        {
            if (_childSubclass.TryGetValue(childHwnd, out entry))
                entry.State.AllowProgrammaticMove = false;
        }
    }

    private const int WindowGap = 12;

    private static bool TryFindFreePosition(
        RECT mainRect,
        int width,
        int height,
        RECT workArea,
        IReadOnlyList<RECT> occupied,
        out int bestX,
        out int bestY)
    {
        var bestScore = long.MaxValue;
        var selectedX = workArea.Left;
        var selectedY = workArea.Top;

        void Consider(int x, int y)
        {
            var candidate = new RECT
            {
                Left = x,
                Top = y,
                Right = x + width,
                Bottom = y + height,
            };

            if (candidate.Left < workArea.Left || candidate.Top < workArea.Top
                || candidate.Right > workArea.Right || candidate.Bottom > workArea.Bottom)
                return;

            foreach (var existing in occupied)
            {
                if (RectanglesOverlap(candidate, existing))
                    return;
            }

            var score = PlacementScore(candidate, mainRect);
            if (score < bestScore)
            {
                bestScore = score;
                selectedX = candidate.Left;
                selectedY = candidate.Top;
            }
        }

        foreach (var anchor in occupied)
        {
            var centeredY = anchor.Top + ((anchor.Bottom - anchor.Top) - height) / 2;
            var bottomAlignedY = anchor.Bottom - height;
            var centeredX = anchor.Left + ((anchor.Right - anchor.Left) - width) / 2;
            var rightAlignedX = anchor.Right - width;

            Consider(anchor.Right + WindowGap, anchor.Top);
            Consider(anchor.Right + WindowGap, centeredY);
            Consider(anchor.Right + WindowGap, bottomAlignedY);
            Consider(anchor.Left - width - WindowGap, anchor.Top);
            Consider(anchor.Left - width - WindowGap, centeredY);
            Consider(anchor.Left - width - WindowGap, bottomAlignedY);
            Consider(anchor.Left, anchor.Bottom + WindowGap);
            Consider(centeredX, anchor.Bottom + WindowGap);
            Consider(rightAlignedX, anchor.Bottom + WindowGap);
            Consider(anchor.Left, anchor.Top - height - WindowGap);
            Consider(centeredX, anchor.Top - height - WindowGap);
            Consider(rightAlignedX, anchor.Top - height - WindowGap);
        }

        const int scanStep = 16;
        for (var y = workArea.Top; y <= workArea.Bottom - height; y += scanStep)
        {
            for (var x = workArea.Left; x <= workArea.Right - width; x += scanStep)
                Consider(x, y);

            Consider(workArea.Right - width, y);
        }

        for (var x = workArea.Left; x <= workArea.Right - width; x += scanStep)
            Consider(x, workArea.Bottom - height);

        Consider(workArea.Right - width, workArea.Bottom - height);
        bestX = selectedX;
        bestY = selectedY;
        return bestScore != long.MaxValue;
    }

    private static bool RectanglesOverlap(RECT first, RECT second) =>
        first.Left < second.Right && first.Right > second.Left
        && first.Top < second.Bottom && first.Bottom > second.Top;

    private static long PlacementScore(RECT candidate, RECT mainRect)
    {
        var horizontalGap = candidate.Left >= mainRect.Right
            ? candidate.Left - mainRect.Right
            : mainRect.Left >= candidate.Right
                ? mainRect.Left - candidate.Right
                : 0;
        var verticalGap = candidate.Top >= mainRect.Bottom
            ? candidate.Top - mainRect.Bottom
            : mainRect.Top >= candidate.Bottom
                ? mainRect.Top - candidate.Bottom
                : 0;

        var candidateCenterX = candidate.Left + (candidate.Right - candidate.Left) / 2;
        var candidateCenterY = candidate.Top + (candidate.Bottom - candidate.Top) / 2;
        var mainCenterX = mainRect.Left + (mainRect.Right - mainRect.Left) / 2;
        var mainCenterY = mainRect.Top + (mainRect.Bottom - mainRect.Top) / 2;
        var centerDistanceX = candidateCenterX - mainCenterX;
        var centerDistanceY = candidateCenterY - mainCenterY;

        return (long)horizontalGap * horizontalGap
            + (long)verticalGap * verticalGap
            + (long)centerDistanceX * centerDistanceX / 8
            + (long)centerDistanceY * centerDistanceY / 8;
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
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

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