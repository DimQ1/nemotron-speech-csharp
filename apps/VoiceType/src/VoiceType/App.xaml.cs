using System.IO;
using System.Windows;
using System.Windows.Threading;
using VoiceType.Services;
using VoiceType.ViewModels;

namespace VoiceType;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Log unhandled exceptions
        DispatcherUnhandledException += (s, args) =>
        {
            Console.Error.WriteLine($"!!! DISPATCHER EXCEPTION: {args.Exception}");
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

        // Ensure app data directories exist (data/ next to the executable)
        AppPaths.EnsureDataRoot();
        AppPaths.EnsureSessionsDir();
        AppPaths.EnsureModelsDir();

        // Save settings on exit to persist any state not captured by individual property setters
        Exit += (s, args) =>
        {
            if (MainWindow is Views.MainWindow mainWindow && mainWindow.DataContext is MainViewModel vm)
                SettingsService.Save(vm.Settings);
        };
    }
}
