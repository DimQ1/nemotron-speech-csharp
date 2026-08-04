using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class TimePlugin : IAssistantPlugin
{
    public string Id => "time";
    public string Name => "Time";
    public string Description => "Reports the current local time.";

    public bool CanHandle(string utterance) =>
        utterance.Contains("который час", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("сколько времени", StringComparison.OrdinalIgnoreCase);

    public Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken)
    {
        var now = DateTime.Now.ToString("HH:mm");
        return Task.FromResult(new AssistantResponse($"Сейчас {now}"));
    }
}
