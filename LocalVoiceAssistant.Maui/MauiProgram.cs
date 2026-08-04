using Microsoft.Extensions.Logging;
using Plugin.Maui.Audio;

namespace LocalVoiceAssistant.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.AddAudio(
				configureStreamerOptions: options =>
				{
					options.SampleRate = 16000;
					options.Channels = ChannelType.Mono;
					options.BitDepth = BitDepth.Pcm16bit;
				})
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
