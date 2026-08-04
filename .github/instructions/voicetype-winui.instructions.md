---
name: "VoiceType.WinUI Project Instructions"
description: "Use when working on the VoiceType.WinUI WinUI 3 application, including C#, XAML, MVVM, Win32 interop, taskbar indicators, data paths, or MSIX packaging."
applyTo: "apps/VoiceType.WinUI/**"
---

# VoiceType.WinUI Project Rules

## Platform and UI

- This is a WinUI 3 desktop app targeting .NET 10 and Windows App SDK 2.3.1. Use `Microsoft.UI.*` namespaces and existing project abstractions.
- Use `App.MainWindow` for the main window reference. Do not use UWP-only APIs such as `Window.Current`, `ApplicationView`, or `GetForCurrentView()`.
- Use `AppWindow` and `OverlappedPresenter` for window sizing, positioning, presenter state, and always-on-top behavior.
- Use `DispatcherQueue.TryEnqueue` for UI work raised by services, recognition callbacks, timers, or Win32 callbacks.
- Set `ContentDialog.XamlRoot` before showing a dialog.

## MVVM and Services

- Use CommunityToolkit.Mvvm source generators already used by the project (`ObservableObject`, `[ObservableProperty]`, and `[RelayCommand]`). Keep recognition, persistence, audio, and Win32 logic in services or ViewModels rather than code-behind.
- Preserve the existing DI composition root in `App.ConfigureServices`; prefer registered interfaces and existing services over direct construction.
- ViewModels must handle expected operation failures locally and update user-visible state. Do not rely on `AsyncRelayCommand` to surface exceptions because its implementation logs exceptions through `Debug.WriteLine`.
- Keep nullable reference types and implicit usings enabled. Use file-scoped namespaces and `_camelCase` private fields.

## Window Management and Interop

- Child windows use both their in-process singleton/open-instance guard and `Services/ChildWindowGuard` for cross-process protection. Acquire the global guard before constructing a child window and release it when the window closes.
- `MainWindow.TrackChildWindow` should place a child beside the main window only during initial activation. Do not reposition it on every activation; preserve user moves.
- Preserve the existing child-window subclassing behavior that blocks non-user repositioning while allowing manual drag and resize. Remove the subclass when the child closes.
- Keep Win32 delegate instances alive for as long as the native callback can invoke them, and release hooks, hotkeys, timers, and COM/taskbar resources during window shutdown.

## Recording and Taskbar State

- The taskbar microphone overlay represents `MainViewModel.IsRecording`, not text injection. Use `IsCaptureMuted` only to select the muted visual state.
- Keep the taskbar indicator lifecycle tied to the main window lifecycle: initialize after the HWND is available, update on relevant property changes, and stop/dispose on `Closed`.
- `IsActivelyInjecting` describes text injection and must not be used as a proxy for microphone recording.

## Application Data

- Use `Services/AppPaths` as the single source of truth for settings, models, sessions, logs, and temporary files. Do not introduce parallel path construction.
- For packaged MSIX runs, use `AppPaths.DataRoot`; do not replace it with `Windows.Storage.ApplicationData.Current.LocalFolder`, which points to a different package folder than the redirected local app-data location used by this app.
- Preserve existing settings and model data when updating the installed package unless the user explicitly requests a reset. Use `reset-dev-data.ps1` for deliberate clean-install simulations.

## Build, Package, and Verify

- For a fast local build, use:

  ```powershell
  dotnet build apps/VoiceType.WinUI/src/VoiceType.WinUI/VoiceType.WinUI.csproj -c Debug -p:GpuArch=CPU
  ```

- For a release MSIX, use `apps/VoiceType.WinUI/src/VoiceType.WinUI/build-store-release.ps1`. Pass `-Sign -CertThumbprint <thumbprint>` only when a suitable certificate is available; never hard-code private keys or passwords in the repository.
- Install local signed packages with `apps/VoiceType.WinUI/src/VoiceType.WinUI/install-dev.ps1` from an elevated PowerShell when certificate trust requires machine-level stores.
- A package with the same identity and version but different contents cannot be installed as an update. Bump `Package.appxmanifest` version for a new package, or remove the existing package deliberately while preserving data where possible.
- After changes, run the narrowest relevant build or test first, then inspect `git diff --check`. Do not commit or push unless explicitly requested.