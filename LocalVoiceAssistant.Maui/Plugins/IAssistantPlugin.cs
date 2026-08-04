using LocalVoiceAssistant.Maui.Core;
using LocalVoiceAssistant.Maui.Models;

namespace LocalVoiceAssistant.Maui.Plugins;

public interface IAssistantPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    bool CanHandle(string utterance);
    Task<AssistantResponse> ExecuteAsync(string utterance, CancellationToken cancellationToken);

    PluginManifest Manifest => new(
        Id,
        Name,
        "1.0.0",
        Description,
        new HashSet<PluginCapability> { PluginCapability.Utility },
        new HashSet<PluginPermission>());

    async Task<ToolResult> InvokeAsync(
        ToolInvocation invocation,
        IProgress<PluginProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new PluginProgress(null, $"Запускаю «{Name}»..."));
        var response = await ExecuteAsync(invocation.Input, cancellationToken).ConfigureAwait(false);
        progress?.Report(new PluginProgress(1, response.Message));
        return new ToolResult(
            !response.IsWarning,
            response.Message,
            response.IsWarning ? "plugin_error" : null,
            ShouldStop: response.ShouldStop);
    }
}
