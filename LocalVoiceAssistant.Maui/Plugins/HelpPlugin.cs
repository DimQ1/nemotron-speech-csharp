using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class HelpPlugin : IAssistantPlugin
{
    public string Id => "help";
    public string Name => "Help";
    public string Description => "Explains the available voice commands.";

    public bool CanHandle(string utterance) =>
        utterance.Contains("помощь", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("что ты умеешь", StringComparison.OrdinalIgnoreCase);

    public Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken)
    {
        var message = "Я могу: открыть браузер, искать в интернете, сообщить время или дату, и остановить сессию.";
        return Task.FromResult(new AssistantResponse(message));
    }
}
