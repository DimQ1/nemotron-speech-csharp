using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public sealed class BrowserPlugin : IAssistantPlugin
{
    public string Id => "browser";
    public string Name => "Browser";
    public string Description => "Opens a browser tab or the default search page.";

    public bool CanHandle(string utterance) =>
        utterance.Contains("открой", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("браузер", StringComparison.OrdinalIgnoreCase) ||
        utterance.Contains("сайт", StringComparison.OrdinalIgnoreCase);

    public async Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken)
    {
        var target = "https://www.google.com";
        if (utterance.Contains("поиск", StringComparison.OrdinalIgnoreCase) || utterance.Contains("найди", StringComparison.OrdinalIgnoreCase))
        {
            var query = utterance.Replace("найди", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("поиск", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("в интернете", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
            target = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        }

        await Browser.Default.OpenAsync(new Uri(target), BrowserLaunchMode.SystemPreferred);
        return new AssistantResponse($"Открыл ссылку: {target}");
    }
}
