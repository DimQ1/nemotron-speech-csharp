using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OpenTelemetry;
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

    /// <summary>OpenTelemetry provider — disposed on shutdown.</summary>
    private static IDisposable? _openTelemetry;

    /// <summary>Local telemetry service for structured error logging.</summary>
    public static ISystemTelemetry Telemetry => Services.GetRequiredService<ISystemTelemetry>();

    public App()
    {
        InitializeComponent();

        UnhandledException += (s, args) =>
        {
            var exStr = args.Exception.ToString();
            Console.Error.WriteLine($"!!! UNHANDLED EXCEPTION: {exStr}");

            try
            {
                AppPaths.EnsureDataRoot();
                File.AppendAllText(AppPaths.ErrorLogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UNHANDLED] {exStr}\n");
            }
            catch { }

            if (Services is not null)
            {
                try { Telemetry.LogError("App", "Unhandled UI exception", args.Exception); }
                catch { }
            }

            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            var exStr = ex?.ToString() ?? "Unknown error";
            Console.Error.WriteLine($"!!! APPDOMAIN UNHANDLED EXCEPTION: {exStr}");

            if (Services is not null)
            {
                try { Telemetry.LogError("AppDomain", "Unhandled domain exception", ex); }
                catch { }
            }
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            args.SetObserved();
            Console.Error.WriteLine($"!!! UNOBSERVED TASK EXCEPTION: {args.Exception}");

            if (Services is not null)
            {
                try { Telemetry.LogError("Task", "Unobserved task exception", args.Exception); }
                catch { }
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Environment.SetEnvironmentVariable("ORT_DISABLE_MODEL_VALIDATION", "1");

        // Ensure development telemetry defaults are set before DI configures logging.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:18890/");
        }

        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        // Start OpenTelemetry SDK (exports to Aspire Dashboard in dev mode)
        if (TelemetryConfiguration.IsOtlpExportEnabled())
        {
            _openTelemetry = TelemetryConfiguration.StartOpenTelemetrySdk();
            var endpoint = TelemetryConfiguration.GetOtlpEndpoint();
            Telemetry.LogInfo("App", $"OpenTelemetry SDK started, OTLP endpoint: {endpoint}");
        }
        else
        {
            Telemetry.LogInfo("App", "OpenTelemetry SDK NOT started (no OTLP endpoint / not dev mode)");
        }

        Telemetry.LogInfo("App", "VoiceType.WinUI started");

        MainWindow = Services.GetRequiredService<Views.MainWindow>();
        MainWindow.ConfigureWindow();
        MainWindow.Activate();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // ---- Logging (structured, with OTLP export to Aspire Dashboard) ----
        services.AddLogging(builder => TelemetryConfiguration.ConfigureLogging(builder));

        // ---- Infrastructure ----
        services.AddSingleton<DispatcherQueue>(_ => DispatcherQueue.GetForCurrentThread());
        services.AddSingleton<IAppPaths, AppPathsAdapter>();
        services.AddSingleton<ISystemTelemetry, ErrorTelemetryService>();

        // ---- Services ----
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<IPostProcessingPipeline, PostProcessingPipeline>();
        services.AddSingleton<IGlobalHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<ITextInjector, TextInjector>();

        // Recognition (decorated with logging)
        services.AddSingleton<RecognitionService>(sp =>
            new RecognitionService(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IPostProcessingPipeline>(),
                sp.GetRequiredService<ISessionManager>(),
                sp.GetService<ISystemTelemetry>()));
        services.AddSingleton<IRecognitionService>(sp =>
            new LoggingRecognitionService(
                sp.GetRequiredService<RecognitionService>(),
                sp.GetRequiredService<ISystemTelemetry>()));

        services.AddTransient<IGlobalInputHook, GlobalInputHook>();
        services.AddTransient<IModelDownloaderService, ModelDownloaderService>();
        services.AddSingleton<IWindowInterop, WindowInterop>();

        // ---- ViewModels ----
        services.AddTransient<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ModelDownloaderViewModel>();

        // ---- Views ----
        services.AddTransient<Views.MainWindow>();
    }
}