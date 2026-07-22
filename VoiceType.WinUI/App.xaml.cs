using Microsoft.UI.Xaml;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI;

public partial class App : Application
{
    /// <summary>Static reference to the main window (WinUI 3 best practice).</summary>
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
        MainWindow = new Views.MainWindow();
        MainWindow.ConfigureWindow();
        MainWindow.Activate();
    }
}
