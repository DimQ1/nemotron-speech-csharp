---
description: "WinUI 3 / Windows App SDK official documentation sources. Use when working with WinUI 3 windowing, XAML, controls, or Windows App SDK APIs."
name: "WinUI 3 Official Documentation"
applyTo: "**/*.xaml, **/*.cs, **/*.csproj"
---

# WinUI 3 / Windows App SDK — Official Documentation Sources

## Primary Documentation Hub

- **WinUI 3 Overview**: https://learn.microsoft.com/en-us/windows/apps/winui/winui3/
- **Windows App SDK**: https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/
- **API Reference**: https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/

## Windowing & App Lifecycle

| Topic | URL |
|-------|-----|
| Windowing overview | https://learn.microsoft.com/en-us/windows/apps/develop/ui/windowing-overview |
| Manage app windows | https://learn.microsoft.com/en-us/windows/apps/develop/ui/manage-app-windows |
| Title bar customization | https://learn.microsoft.com/en-us/windows/apps/develop/title-bar |
| App instancing / lifecycle | https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle |

## XAML & Controls

| Topic | URL |
|-------|-----|
| XAML controls gallery | https://learn.microsoft.com/en-us/windows/apps/design/controls/ |
| XAML styles | https://learn.microsoft.com/en-us/windows/apps/design/style/ |
| {x:Bind} markup extension | https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/x-bind-markup-extension |
| Dependency properties | https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/dependency-properties-overview |
| Custom attached properties | https://learn.microsoft.com/en-us/windows/uwp/xaml-platform/custom-attached-properties |

## Layout & Design

| Topic | URL |
|-------|-----|
| Layout panels | https://learn.microsoft.com/en-us/windows/apps/design/layout/layout-panels |
| Screen sizes & breakpoints | https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design |
| Mica & Acrylic materials | https://learn.microsoft.com/en-us/windows/apps/design/style/mica |
| Fluent Design System | https://learn.microsoft.com/en-us/windows/apps/design/fluent-design-system/ |

## Data Binding & MVVM

| Topic | URL |
|-------|-----|
| Data binding overview | https://learn.microsoft.com/en-us/windows/apps/develop/data-binding/ |
| MVVM Toolkit | https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/ |
| ObservableProperty | https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/generators/observableproperty |
| RelayCommand | https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/generators/relaycommand |

## Deployment & Packaging

| Topic | URL |
|-------|-----|
| Package & deploy | https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/ |
| MSIX packaging | https://learn.microsoft.com/en-us/windows/msix/ |
| Single-project MSIX | https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix |

## Samples & Reference Apps

- **WinUI 3 Gallery** (Microsoft Store): https://apps.microsoft.com/detail/9P3JFPWWDZRC
- **WinUI 3 Gallery** (GitHub): https://github.com/microsoft/WinUI-Gallery
- **Windows App SDK Samples**: https://github.com/microsoft/WindowsAppSDK-Samples

## Key Namespaces (WinUI 3 vs UWP)

| WinUI 3 (correct) | UWP (legacy, DO NOT USE) |
|-------------------|--------------------------|
| `Microsoft.UI.Xaml.*` | `Windows.UI.Xaml.*` |
| `Microsoft.UI.Windowing.*` | `Windows.UI.WindowManagement.*` |
| `Microsoft.UI.Composition.*` | `Windows.UI.Composition.*` |
| `Microsoft.UI.Colors` | `Windows.UI.Colors` |
| `Microsoft.UI.Dispatching.DispatcherQueue` | `Windows.UI.Core.CoreDispatcher` |

## Critical Migration Notes

- **NEVER** use `Window.Current` — track windows via `App.MainWindow` static property
- **NEVER** use `CoreDispatcher.RunAsync` — use `DispatcherQueue.TryEnqueue`
- **NEVER** use `ApplicationView` — use `AppWindow` for window management
- **NEVER** use `GetForCurrentView()` patterns — they don't exist in WinUI 3 desktop
- **ALWAYS** use `AppWindow.Resize()` / `AppWindow.Move()` for window sizing/positioning
- **ALWAYS** use `OverlappedPresenter` for min/max size, always-on-top, and resizable settings
- **ALWAYS** set `dialog.XamlRoot = this.Content.XamlRoot` before showing ContentDialog

## Effective Pixels vs Device Pixels

- **XAML** uses *effective pixels* (epx) — virtual units independent of screen density
- **AppWindow** uses *device pixels* — physical pixels on screen
- Convert between them using `XamlRoot.RasterizationScale` when needed

## Common Patterns

### Setting window size in constructor
```csharp
public MainWindow()
{
    InitializeComponent();
    
    // AppWindow is available immediately after Window creation in WASDK 1.4+
    if (AppWindow is not null)
    {
        AppWindow.Resize(new SizeInt32(372, 600));
        
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 320;
            presenter.PreferredMinimumHeight = 516;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = false;
        }
    }
}
```

### Getting AppWindow from HWND (legacy interop)
```csharp
var hwnd = WindowNative.GetWindowHandle(this);
var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
var appWindow = AppWindow.GetFromWindowId(windowId);
```
