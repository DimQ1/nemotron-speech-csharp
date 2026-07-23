using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.Services.Recognition;
using VoiceType.WinUI.ViewModels;

namespace VoiceType.WinUI;

public partial class App : Application
{
    /// <summary>DI service provider — Composition Root for the application.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Static reference to the main window.</summary>
    public static Views.MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        UnhandledException += (s, args) =>
        {
            Console.Error.WriteLine($"!!! UNHANDLED EXCEPTION: {args.Exception}");
            AppPaths.EnsureDataRoot();
            File.AppendAllText(AppPaths.ErrorLogFile,
                $"[{DateTime.Now}] {args.Exception}\n");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Console.Error.WriteLine($"!!! UNHANDLED EXCEPTION: {ex}");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Environment.SetEnvironmentVariable("ORT_DISABLE_MODEL_VALIDATION", "1");

        // Build DI container
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Resolve MainWindow from DI
        MainWindow = Services.GetRequiredService<Views.MainWindow>();
        MainWindow.ConfigureWindow();
        MainWindow.Activate();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ---- Infrastructure ----
        services.AddSingleton<DispatcherQueue>(_ => DispatcherQueue.GetForCurrentThread());
        services.AddSingleton<IAppPaths, AppPathsAdapter>();

        // ---- Services (singleton — shared state across app lifetime) ----
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<ITextInjector, TextInjector>();

        // Recognition — decorated with logging
        services.AddSingleton<RecognitionService>(sp =>
            new RecognitionService(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IPostProcessingPipeline>(),
                sp.GetRequiredService<ISessionManager>()));
        services.AddSingleton<IRecognitionService>(sp =>
            new LoggingRecognitionService(sp.GetRequiredService<RecognitionService>()));

        // Input hook — per-instance (re-installed on each session)
        services.AddTransient<IGlobalInputHook, GlobalInputHook>();

        // Downloader — per-window
        services.AddTransient<IModelDownloaderService, ModelDownloaderService>();

        // ---- ViewModels (transient — new instance per window) ----
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ModelDownloaderViewModel>();

        // ---- Views (transient) ----
        services.AddTransient<Views.MainWindow>();
    }
}