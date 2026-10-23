using Uno.UI.Hosting;

namespace VoiceType.Uno.iOS;

public class EntryPoint
{
    // The Skia renderer runs on UIKit; the host creates the UIApplication and its delegate.
    public static void Main(string[] args)
    {
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseAppleUIKit()
            .Build();

        host.Run();
    }
}
