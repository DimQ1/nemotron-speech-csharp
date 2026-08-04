using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class StopPlugin : IAssistantPlugin
{
    public string Id => "stop";
    public string Name => "Stop";
    public string Description => "Stops the current listening session.";

    public bool CanHandle(string utterance) =>
        utterance.Contains("стоп", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("хватит", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("stop", StringComparison.OrdinalIgnoreCase);

    public Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken)
        => Task.FromResult(new AssistantResponse("Останавливаю сессию.", ShouldStop: true));
}
