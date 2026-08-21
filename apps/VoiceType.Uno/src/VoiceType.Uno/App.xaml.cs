using System.Diagnostics.CodeAnalysis;
using Uno.Resizetizer;
#if VOICE_TYPE_WINDOWS
using SpeechLib.Audio;
#endif
using VoiceType.Hotkeys;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Audio;
using VoiceType.Uno.Services.Platform;
using VoiceType.Uno.Services.Platform.Linux;

namespace VoiceType.Uno;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        this.InitializeComponent();
    }

    protected Window? MainWindow { get; private set; }
    protected IHost? Host { get; private set; }

    /// <summary>Composition Root — service provider for the running app.</summary>
    public static IServiceProvider Services =>
        (Current as App)?.Host?.Services
        ?? throw new InvalidOperationException("App host is not initialized yet.");

    [SuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Uno.Extensions APIs are used in a way that is safe for trimming in this template context.")]
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var builder = this.CreateBuilder(args)
            .Configure(host => host
#if DEBUG
                // Switch to Development environment when running in DEBUG
                .UseEnvironment(Environments.Development)
#endif
                .ConfigureServices((context, services) =>
                {
                    // ---- Platform-independent services ----
                    services.AddSingleton<SettingsService>();
                    services.AddSingleton<ModelDownloadService>();
#if VOICE_TYPE_WINDOWS
                    services.AddSingleton<SpeechLib.IAudioSourceFactory, NAudio3AudioSourceFactory>();
#else
                    services.AddSingleton<SpeechLib.IAudioSourceFactory, PulseAudioSourceFactory>();
#endif
                    services.AddSingleton<RecognitionService>();

                    // ---- Platform abstractions (backends selected per-OS) ----
                    // Global hotkeys: XDG GlobalShortcuts portal on Linux
                    // (Wayland + X11, xdg-desktop-portal >= 1.18); Null Object fallback.
                    // Note: portal connection is async — use Null for startup;
                    // the ViewModel can swap in the real backend on a background task.
                    services.AddSingleton<IGlobalHotkeyService>(_ => new NullGlobalHotkeyService());
                    // Text injection: SendInput+clipboard on Windows; on Linux the
                    // injector picks clipboard (wl-copy/xclip/xsel) + keyboard
                    // (XTest on X11, ydotool on Wayland) backends per session.
                    services.AddSingleton<IPlatformTextInjector>(_ =>
                        OperatingSystem.IsWindows() ? new WindowsTextInjector()
                        : OperatingSystem.IsLinux() ? new LinuxTextInjector()
                        : new NullTextInjector());
                    // Tray recording indicator: StatusNotifierItem on Linux
                    // (GNOME AppIndicator / KDE Plasma); Null elsewhere.
                    services.AddSingleton<ITrayIndicator>(_ =>
                        OperatingSystem.IsLinux() ? new LinuxTrayIndicator() : new NullTrayIndicator());

                    // ---- ViewModels ----
                    services.AddSingleton<MainViewModel>();
                })
            );
        MainWindow = builder.Window;

        #if DEBUG
        MainWindow.UseStudio();
#endif
                MainWindow.SetWindowIcon();

        Host = builder.Build();

        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        if (MainWindow.Content is not Frame rootFrame)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // Place the frame in the current Window
            MainWindow.Content = rootFrame;
        }

        if (rootFrame.Content == null)
        {
            // When the navigation stack isn't restored navigate to the first page,
            // configuring the new page by passing required information as a navigation
            // parameter
            rootFrame.Navigate(typeof(MainPage), args.Arguments);
        }
        // Ensure the current window is active
        MainWindow.Activate();
    }
}
