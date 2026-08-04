using LVA.Core.Configuration;
using LVA.Core.Events;
using LVA.Plugins.Abstractions;
using LVA.Plugins.Host;

namespace LVA.Orchestration;

/// <summary>
/// Orchestrator (TZ §2, §5): QuestionReady → ToolRouter → plugin execution
/// with TaskTracker lifecycle and NotificationHub statuses.
/// </summary>
public sealed class Orchestrator
{
    private readonly PluginHost _pluginHost;
    private readonly ToolRouter _router;
    private readonly TaskTracker _taskTracker;
    private readonly NotificationHub _notificationHub;
    private readonly PluginOptions _options;

    public Orchestrator(PluginHost pluginHost, ToolRouter router,
        TaskTracker taskTracker, NotificationHub notificationHub, PluginOptions options)
    {
        _pluginHost = pluginHost;
        _router = router;
        _taskTracker = taskTracker;
        _notificationHub = notificationHub;
        _options = options;
    }

    public TaskTracker Tasks => _taskTracker;

    /// <summary>Handles a QuestionReady event end-to-end.</summary>
    public async Task<ToolResultEvent?> HandleQuestionAsync(QuestionReadyEvent question, CancellationToken ct = default)
    {
        var plugins = _pluginHost.Plugins.Select(p => (p.Plugin.Manifest.Id, p.Plugin.Manifest));
        var decision = _router.Route(question, plugins);

        if (decision is null)
            return null; // no plugin matched — caller may answer locally or ignore

        var plugin = _pluginHost.Plugins.First(p => p.Plugin.Manifest.Id == decision.PluginId).Plugin;
        var task = _taskTracker.Start(decision.PluginId, question.Context);
        await _notificationHub.TaskStartedAsync(task.Id, question.Context, ct);

        var progress = new Progress<TaskProgress>(p =>
        {
            _taskTracker.ReportProgress(task.Id, p.Percent, p.Message);
            _ = _notificationHub.PublishAsync(
                new UserNotification(NotificationKind.TaskProgress, p.Message, task.Id), CancellationToken.None);
        });

        ToolResult result;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.DefaultTimeoutSec));
        try
        {
            result = await plugin.ExecuteAsync(decision.Invocation, progress, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ToolResult.Fail($"превышен таймаут {_options.DefaultTimeoutSec} с");
        }
        catch (Exception ex)
        {
            result = ToolResult.Fail(ex.Message);
        }

        var resultEvent = new ToolResultEvent(task.Id, result.Success,
            result.ResultMarkdown, result.Error, result.Sources ?? Array.Empty<string>(),
            DateTimeOffset.UtcNow);

        _taskTracker.Complete(task.Id, resultEvent);
        if (result.Success)
            await _notificationHub.TaskCompletedAsync(task.Id, ct);
        else
            await _notificationHub.TaskFailedAsync(task.Id, result.Error ?? "неизвестная ошибка", ct);

        return resultEvent;
    }
}
