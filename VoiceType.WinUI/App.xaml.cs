using Microsoft.UI.Xaml;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI;

public partial class App : Application
{
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
        var window = new Views.MainWindow();
        window.Activate();
    }
}
