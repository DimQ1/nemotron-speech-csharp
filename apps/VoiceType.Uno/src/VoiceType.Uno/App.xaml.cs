using System.Diagnostics.CodeAnalysis;
using Uno.Resizetizer;
using VoiceType.Hotkeys;
using VoiceType.Hotkeys.Windows;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Audio;
using VoiceType.Uno.Services.Platform;
using VoiceType.Uno.Services.Platform.Linux;
using SpeechLib.LiteRT;

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
                    // Parallel model download queue (ASR + translation at the same
                    // time, aggregated progress for the whole queue).
                    services.AddSingleton<DownloadQueueService>();
#if VOICE_TYPE_WINDOWS
                    services.AddSingleton<SpeechLib.IAudioSourceFactory, SpeechLib.Audio.NAudio3AudioSourceFactory>();
#elif __ANDROID__
                    // Android head: microphone capture via AudioRecord
                    // (Services/Audio/AndroidAudioSourceFactory.cs). Loopback/Mix are
                    // unavailable without elevated privileges — Mic only.
                    services.AddSingleton<SpeechLib.IAudioSourceFactory, AndroidAudioSourceFactory>();
#else
                    // Skia desktop head. Audio capture is picked at runtime:
                    //   Windows → NAudio 3.0.1 (WASAPI) so dictation works on a dev box;
                    //   Linux   → PulseAudio (libpulse-simple) for the real target.
                    services.AddSingleton<SpeechLib.IAudioSourceFactory>(_ =>
                        OperatingSystem.IsWindows()
                            ? (SpeechLib.IAudioSourceFactory)new SpeechLib.Audio.NAudio3AudioSourceFactory()
                            : new PulseAudioSourceFactory());
#endif
                    services.AddSingleton<RecognitionService>();

                    // ---- Platform abstractions (backends selected per-OS) ----
                    // Global hotkeys: Windows → RegisterHotKey (message-only
                    // window); Linux → XDG GlobalShortcuts portal (swapped in
                    // asynchronously by the ViewModel); Null elsewhere.
                    services.AddSingleton<IGlobalHotkeyService>(_ =>
                        OperatingSystem.IsWindows()
                            ? (IGlobalHotkeyService)new WindowsGlobalHotkeyService()
                            : new NullGlobalHotkeyService());
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
                    // Live translation via LiteRT-LM. Two engines, picked from settings:
                    //   native — in-process model (LiteRtLmSharp natives, no sidecar;
                    //            win-x64 + linux-x64), preferred; falls back to http
                    //            when the .litertlm model is not downloaded.
                    //   http   — external OpenAI-compatible server at TranslationServerUrl.
                    services.AddSingleton(sp =>
                    {
                        var settings = sp.GetRequiredService<SettingsService>().Load();
#if __ANDROID__
                        // LiteRtLmSharp natives ship for win-x64 / linux-x64 only, so the
                        // in-process native backend cannot run on Android — always use the
                        // external LiteRT-LM server (HTTP backend) there.
                        return new TranslationService(
                            new LiteRTLmOptions { BaseUrl = settings.TranslationServerUrl },
                            TranslationService.BackendKind.Http);
#else
                        var backend = string.Equals(settings.TranslationBackend, "http", StringComparison.OrdinalIgnoreCase)
                            ? TranslationService.BackendKind.Http
                            : TranslationService.BackendKind.Native;
                        return new TranslationService(
                            new LiteRTLmOptions { BaseUrl = settings.TranslationServerUrl },
                            backend);
#endif
                    });

                    // ---- ViewModels ----
                    services.AddSingleton<MainViewModel>();
                })
            );
        MainWindow = builder.Window;

        #if DEBUG
        MainWindow.UseStudio();
#endif

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
        // Apply the app icon now that the native window handle exists.
        MainWindow.SetWindowIcon();
    }
}
