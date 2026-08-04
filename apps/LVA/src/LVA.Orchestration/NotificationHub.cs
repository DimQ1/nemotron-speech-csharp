namespace LVA.Orchestration;

/// <summary>Kind of user-facing notification (TZ §5.2, §14.11).</summary>
public enum NotificationKind
{
    ListeningStarted,
    Activated,
    TaskStarted,
    TaskProgress,
    TaskCompleted,
    TaskFailed,
    Cancelled
}

/// <summary>A user-facing status message with its semantic kind.</summary>
public sealed record UserNotification(NotificationKind Kind, string Message, Guid? TaskId = null);

/// <summary>Sink of user notifications (UI banner, sounds, system tray, TTS).</summary>
public interface INotificationSink
{
    Task NotifyAsync(UserNotification notification, CancellationToken ct = default);
}

/// <summary>
/// Notification hub: translates task-lifecycle events into user notifications
/// and fans them out to registered sinks (UI, sounds, system notifications).
/// </summary>
public sealed class NotificationHub
{
    private readonly List<INotificationSink> _sinks = new();

    public void RegisterSink(INotificationSink sink) => _sinks.Add(sink);

    public async Task PublishAsync(UserNotification notification, CancellationToken ct = default)
    {
        foreach (var sink in _sinks)
            await sink.NotifyAsync(notification, ct);
    }

    // Convenience publishers mirroring TZ §5.2 messages.

    public Task TaskStartedAsync(Guid taskId, string description, CancellationToken ct = default)
        => PublishAsync(new UserNotification(NotificationKind.TaskStarted,
            $"Работаю над вашим запросом: {description}", taskId), ct);

    public Task TaskCompletedAsync(Guid taskId, CancellationToken ct = default)
        => PublishAsync(new UserNotification(NotificationKind.TaskCompleted, "Готово", taskId), ct);

    public Task TaskFailedAsync(Guid taskId, string reason, CancellationToken ct = default)
        => PublishAsync(new UserNotification(NotificationKind.TaskFailed,
            $"Не удалось: {reason}", taskId), ct);
}
