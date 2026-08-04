using LocalVoiceAssistant.Maui.Core;
using LocalVoiceAssistant.Maui.Plugins;

namespace LocalVoiceAssistant.Maui.Services;

public static class LvaRuntimeFactory
{
    public static IModelBootstrapService CreateModelBootstrapService() =>
        new HuggingFaceModelBootstrapService();

    public static VoiceAssistantOrchestrator Create()
    {
        var plugins = new IAssistantPlugin[]
        {
            new WebSearchPlugin(new DuckDuckGoSearchProvider()),
            new BrowserPlugin(),
            new TimePlugin(),
            new DatePlugin(),
            new HelpPlugin(),
            new StopPlugin()
        };
        var host = new PluginHost(plugins);
        var externalPluginDirectory = Path.Combine(FileSystem.AppDataDirectory, "lva", "plugins");
        Directory.CreateDirectory(externalPluginDirectory);
        PluginHost.LoadPluginsFromDirectory(host, externalPluginDirectory);

        return new VoiceAssistantOrchestrator(
            CreateSpeechService(),
            host,
            new ActivationGate(),
            new SqliteConversationStore());
    }

    private static ISpeechRecognitionService CreateSpeechService()
    {
#if WINDOWS
        return new SpeechRecognitionService();
#else
        return new MobileSpeechRecognitionService();
#endif
    }
}
