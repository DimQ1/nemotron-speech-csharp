using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class SearchPlugin : IAssistantPlugin
{
    public string Id => "search";
    public string Name => "Search";
    public string Description => "Searches the web using a search query from the spoken text.";

    public bool CanHandle(string utterance) =>
        utterance.Contains("найди", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("поиск", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("в интернете", StringComparison.OrdinalIgnoreCase);

    public async Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken)
    {
        var query = utterance
            .Replace("найди", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("поиск", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("в интернете", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        var target = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        await Browser.Default.OpenAsync(new Uri(target), BrowserLaunchMode.SystemPreferred);
        return new AssistantResponse($"Запустил поиск: {query}");
    }
}
