using System.Collections.Concurrent;
using LVA.Core.Events;

namespace LVA.Orchestration;

/// <summary>
/// Tracks assistant task lifecycle (TZ §5.2): Started → InProgress →
/// Completed/Failed/Cancelled. Every transition is published as an event.
/// </summary>
public sealed class TaskTracker
{
    private readonly ConcurrentDictionary<Guid, AssistantTask> _tasks = new();

    public event Action<AssistantTask>? TaskStarted;
    public event Action<TaskProgressEvent>? TaskProgressed;
    public event Action<ToolResultEvent, AssistantTask>? TaskCompleted;
    public event Action<ToolResultEvent, AssistantTask>? TaskFailed;

    public IReadOnlyCollection<AssistantTask> ActiveTasks
        => _tasks.Values.Where(t => t.Status is AssistantTaskStatus.Started or AssistantTaskStatus.InProgress).ToArray();

    public AssistantTask Start(string pluginId, string description)
    {
        var task = new AssistantTask(Guid.NewGuid(), pluginId, description,
            AssistantTaskStatus.Started, DateTimeOffset.UtcNow);
        _tasks[task.Id] = task;
        TaskStarted?.Invoke(task);
        return task;
    }

    public void ReportProgress(Guid taskId, int percent, string message)
    {
        if (_tasks.TryGetValue(taskId, out var task))
            _tasks[taskId] = task with { Status = AssistantTaskStatus.InProgress };
        TaskProgressed?.Invoke(new TaskProgressEvent(taskId, percent, message, DateTimeOffset.UtcNow));
    }

    public void Complete(Guid taskId, ToolResultEvent result)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            return;

        if (result.Success)
        {
            _tasks[taskId] = task with { Status = AssistantTaskStatus.Completed };
            TaskCompleted?.Invoke(result, _tasks[taskId]);
        }
        else
        {
            _tasks[taskId] = task with { Status = AssistantTaskStatus.Failed };
            TaskFailed?.Invoke(result, _tasks[taskId]);
        }
    }

    public void Cancel(Guid taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
            _tasks[taskId] = task with { Status = AssistantTaskStatus.Cancelled };
    }
}
