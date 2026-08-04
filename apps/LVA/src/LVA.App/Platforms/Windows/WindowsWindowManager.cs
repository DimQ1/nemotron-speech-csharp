using System.Runtime.InteropServices;
using LVA.App.Companion;
using LVA.App.Services;
using LVA.App.Views;
using LVA.Plugins.Host;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using SkiaSharp;
using WinRT.Interop;
using MauiWindow = Microsoft.Maui.Controls.Window;
using XamlWindow = Microsoft.UI.Xaml.Window;

namespace LVA.App.WinUI;

/// <summary>
/// Windows implementation: companion as a frameless, transparent, always-on-top
/// window draggable across the whole desktop via pointer events.
/// </summary>
public sealed class WindowsWindowManager : IWindowManager
{
    private XamlWindow? _companionWindow;
    private readonly CompanionStateMachine _stateMachine;
    private readonly PluginHost _plugins;
    private readonly IPluginSettingsStore _settingsStore;

    public bool IsCompanionOpen => _companionWindow is not null;
    public Point CompanionPosition { get; set; } = new(120, 120);

    public WindowsWindowManager(CompanionStateMachine stateMachine, PluginHost plugins, IPluginSettingsStore settingsStore)
    {
        _stateMachine = stateMachine;
        _plugins = plugins;
        _settingsStore = settingsStore;
    }

    public void OpenCompanionWindow()
    {
        if (_companionWindow is not null)
            return;

        var page = new CompanionWindowPage(_stateMachine);
        var mauiWindow = new MauiWindow(page) { Title = "LVA Companion" };

        // Apply platform chrome once the native window exists.
        mauiWindow.HandlerChanged += (_, _) =>
        {
            if (mauiWindow.Handler?.PlatformView is not XamlWindow xamlWindow)
                return;

            _companionWindow = xamlWindow;
            var hwnd = WindowNative.GetWindowHandle(xamlWindow);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.SetBorderAndTitleBar(false, false);
            }

            // Strip remaining non-client chrome via Win32 styles (frameless overlay).
            const int GWL_STYLE = -16;
            const long WS_CAPTION = 0x00C00000L;
            const long WS_THICKFRAME = 0x00040000L;
            const long WS_SYSMENU = 0x00080000L;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE);
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
            SetWindowLongPtr(hwnd, GWL_STYLE, style);

            // Per-pixel-alpha layered window via UpdateLayeredWindow: the
            // renderer draws the pet offscreen and owns the visible output.
            // MAUI content is hidden so it doesn't paint behind the pet.
            const int GWL_EXSTYLE = -20;
            const long WS_EX_LAYERED = 0x00080000L;
            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, exStyle);

            if (xamlWindow.Content is Microsoft.UI.Xaml.UIElement rootContent)
                rootContent.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            var scale = GetDpiForWindow(hwnd) / 96.0;
            var sizeW = (int)Math.Round(180 * scale);
            var sizeH = (int)Math.Round(220 * scale);
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                (int)CompanionPosition.X, (int)CompanionPosition.Y, sizeW, sizeH));

            var renderer = new CompanionLayeredRenderer(hwnd, _stateMachine);
            renderer.Start();
            mauiWindow.Destroying += (_, _) => renderer.Dispose();

            page.NativeDragRequested += () =>
            {
                ReleaseCapture();
                SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            };

            InstallCompanionDragHandler(hwnd);

            appWindow.Changed += (s, _) =>
            {
                if (s.Presenter is OverlappedPresenter)
                    CompanionPosition = new Point(s.Position.X, s.Position.Y);
            };
        };

        mauiWindow.Destroying += (_, _) =>
        {
            if (_companionWindow is not null)
                RemoveWindowSubclass(WindowNative.GetWindowHandle(_companionWindow), CompanionSubclassProc, CompanionSubclassId);
            _companionWindow = null;
        };

        Application.Current?.OpenWindow(mauiWindow);
    }

    public void CloseCompanionWindow()
    {
        if (_companionWindow is not null)
        {
            var mauiWindow = Application.Current?.Windows
                .FirstOrDefault(w => w.Title == "LVA Companion");
            if (mauiWindow is not null)
                Application.Current?.CloseWindow(mauiWindow);
        }
        _companionWindow = null;
    }

    public void OpenChatWindow()
    {
        // Chat is the main window; bring it to front.
        var mainWindow = Application.Current?.Windows.FirstOrDefault();
        if (mainWindow is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(mainWindow);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Show(true);
        }
    }

    public void OpenApplicationSettingsWindow()
    {
        var page = new ApplicationSettingsPage(this, _plugins);
        Application.Current?.OpenWindow(new MauiWindow(page)
        {
            Title = "Настройки LVA",
            Width = 560,
            Height = 540
        });
    }

    public void OpenPluginSettingsWindow(string pluginId)
    {
        var plugin = _plugins.Plugins.FirstOrDefault(p => p.Plugin.Manifest.Id == pluginId)?.Plugin;
        if (plugin?.Manifest.Settings is not { } definition)
            return;

        var page = new PluginSettingsPage(plugin.Manifest.Id, definition, _settingsStore);
        Application.Current?.OpenWindow(new MauiWindow(page)
        {
            Title = $"Настройки: {definition.Title}",
            Width = 560,
            Height = 640
        });
    }

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint LWA_COLORKEY = 0x00000001;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const nuint CompanionSubclassId = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint subclassId, IntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    private static void InstallCompanionDragHandler(IntPtr hwnd)
        => SetWindowSubclass(hwnd, CompanionSubclassProc, CompanionSubclassId, IntPtr.Zero);

    private static readonly SubclassProc CompanionSubclassProc = CompanionWindowProc;

    private static IntPtr CompanionWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, IntPtr referenceData)
    {
        if (message == WM_NCHITTEST && GetWindowRect(hwnd, out var rect))
        {
            var scale = GetDpiForWindow(hwnd) / 96.0;
            var screenX = (short)((long)lParam & 0xFFFF);
            var screenY = (short)(((long)lParam >> 16) & 0xFFFF);
            var x = (screenX - rect.Left) / scale;
            var y = (screenY - rect.Top) / scale;

            // Pet occupies the central 128x128 effective-pixel region (plus margin).
            if (x >= 26 && x <= 154 && y >= 46 && y <= 174)
                return new IntPtr(HTCAPTION);
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam,
        nuint subclassId, IntPtr referenceData);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
