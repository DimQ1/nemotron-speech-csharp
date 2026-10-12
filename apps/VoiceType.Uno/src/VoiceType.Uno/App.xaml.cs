using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Dispatching;
using Uno.Resizetizer;
using SpeechLib.LiteRT;
using SpeechLib.ModelDownload;
using VoiceType.Hotkeys;
using VoiceType.Hotkeys.Windows;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation;
using VoiceType.Uno.Services;
using VoiceType.Uno.Services.Audio;
using VoiceType.Uno.Services.Playback;
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
                    // Single owner of the live settings instance and the only writer of
                    // settings.json: the UI thread, model init, download continuations and
                    // the settings dialog all mutate through it.
                    services.AddSingleton<SettingsStore>();
                    // UI dispatcher behind an interface, so the UI-free core
                    // (VoiceType.Uno.Core) can be unit-tested with a deterministic scheduler.
                    services.AddSingleton<IUiScheduler>(_ => new DispatcherQueueUiScheduler(
                        DispatcherQueue.GetForCurrentThread()
                            ?? throw new InvalidOperationException("The UI dispatcher is not available on this thread.")));

                    // One download pipeline, shared with the WinUI app: the Hugging Face
                    // manager from SpeechLib.ModelDownload. Inside WSL the distro cannot
                    // reach huggingface.co, so downloads run through the Windows curl
                    // (WindowsCurlHubTransport); everywhere else plain HTTP is used.
                    services.AddSingleton(_ =>
                    {
                        IHubTransport transport = WindowsCurlHubTransport.TryCreate()
                            ?? (IHubTransport)new HttpHubTransport(HuggingFaceClient.CreateDefaultHttpClient());
                        return new ModelDownloadManager(new HuggingFaceClient(transport));
                    });
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

                    // ---- Recordings history (design docs/design/voicetype-uno-history) ----
                    // One writer per store: recording folders (sessions/, or the
                    // SessionsPath setting), chats (chats/) and the job queue (jobs/queue.json).
                    services.AddSingleton(sp =>
                    {
                        var sessionsPath = sp.GetRequiredService<SettingsStore>().Current.SessionsPath;
                        return new RecordingStore(string.IsNullOrWhiteSpace(sessionsPath) ? AppPaths.SessionsDir : sessionsPath);
                    });
                    services.AddSingleton(_ => new ChatStore(AppPaths.ChatsDir));
                    services.AddSingleton(_ =>
                    {
                        AppPaths.EnsureJobsDir();
                        return new JobQueue(AppPaths.JobQueueFile);
                    });
                    // Startup recovery of crashed recordings + audio retention, off the UI thread.
                    services.AddSingleton<HistoryMaintenance>();
                    // Playback of stored recordings: WASAPI / PulseAudio / AudioTrack, picked at runtime.
                    services.AddSingleton<IAudioPlayerFactory, AudioPlayerFactory>();

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
                    // Background recording: Android silences the mic for background apps, so a
                    // "microphone" foreground service + wake lock runs while recording
                    // (Platforms/Android/RecordingService.cs). Desktop heads need nothing.
#if __ANDROID__
                    services.AddSingleton<IRecordingKeepAlive, VoiceType.Uno.Droid.AndroidRecordingKeepAlive>();
#else
                    services.AddSingleton<IRecordingKeepAlive, NullRecordingKeepAlive>();
#endif
                    // Live translation via LiteRT-LM. Two engines, picked from settings:
                    //   native — in-process model (LiteRtLmSharp natives, no sidecar;
                    //            win-x64 + linux-x64), preferred; falls back to http
                    //            when the .litertlm model is not downloaded.
                    //   http   — external OpenAI-compatible server at TranslationServerUrl.
                    services.AddSingleton(sp =>
                    {
                        var settings = sp.GetRequiredService<SettingsService>().Load();
                        var backend = string.Equals(settings.TranslationBackend, "http", StringComparison.OrdinalIgnoreCase)
                            ? TranslationService.BackendKind.Http
                            : TranslationService.BackendKind.Native;
                        return new TranslationService(
                            new LiteRTLmOptions { BaseUrl = settings.TranslationServerUrl },
                            backend);
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

        // Finalize recordings a crash left behind and apply audio retention before the
        // first recording starts (the view model waits for recovery); never on the UI thread.
        Host.Services.GetRequiredService<HistoryMaintenance>().Start();

        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        var rootFrame = (MainWindow.Content as Grid)?.Children.OfType<Frame>().FirstOrDefault();
        if (rootFrame is null)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // System bars (status bar, navigation/gesture bar, cut-outs) are kept out once,
            // here on a root that is never reloaded. Insets on the pages themselves were
            // lost: page code sets Padding on resize, and SafeArea applies its inset
            // through that same Padding.
            var shell = new Grid
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Resources["ApplicationPageBackgroundThemeBrush"],
                Children = { rootFrame }
            };
            global::Uno.Toolkit.UI.SafeArea.SetInsets(shell, global::Uno.Toolkit.UI.SafeArea.InsetMask.VisibleBounds);
            MainWindow.Content = shell;
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
        // The Skia desktop head otherwise shows the template's "Uno Platform" caption.
        MainWindow.Title = "VoiceType";
    }
}
