using System.IO;
using System.Windows;
using System.Windows.Threading;

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
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VoiceType", "error.log"),
                $"[{DateTime.Now}] {args.Exception}\n");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Console.Error.WriteLine($"!!! UNHANDLED EXCEPTION: {ex}");
        };

        // Ensure settings directory exists
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType");
        Directory.CreateDirectory(dir);
    }
}
