using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class DatePlugin : IAssistantPlugin
{
    public string Id => "date";
    public string Name => "Date";
    public string Description => "Reports the current date.";

    public bool CanHandle(string utterance) =>
        utterance.Contains("какая дата", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("какое число", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("сегодня", StringComparison.OrdinalIgnoreCase) && utterance.Contains("дата", StringComparison.OrdinalIgnoreCase);

    public Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken)
    {
        var now = DateTime.Now.ToString("dddd, dd MMMM yyyy");
        return Task.FromResult(new AssistantResponse($"Сегодня {now}"));
    }
}
