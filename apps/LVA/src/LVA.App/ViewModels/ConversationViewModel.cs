using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LVA.Core.Events;
using LVA.Core.History;
using LVA.Orchestration;

namespace LVA.App.ViewModels;

/// <summary>A chat message bubble.</summary>
public partial class ChatMessageModel : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isUser;
    [ObservableProperty] private bool _isDraft;      // partial ASR hypothesis
    [ObservableProperty] private string _channel = ""; // 🎙 | ⌨
}

/// <summary>A task card in the chat feed.</summary>
public partial class TaskCardModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _result = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isFailed;
}

/// <summary>Display modes (TZ §14.2).</summary>
public enum UiDisplayMode
{
    Chat,
    ChatAndCompanion,
    CompanionInApp,
    CompanionStandalone
}

/// <summary>
/// Single shared conversation view-model: all display modes are views over it
/// (TZ §14.2). Voice and keyboard inputs land in the same pipeline.
/// </summary>
public partial class ConversationViewModel : ObservableObject
{
    private readonly Orchestrator _orchestrator;
    private readonly IChatHistoryStore _history;
    private readonly TaskTracker _taskTracker;

    public ObservableCollection<ChatMessageModel> Messages { get; } = new();
    public ObservableCollection<TaskCardModel> TaskCards { get; } = new();

    [ObservableProperty] private string _inputText = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isPluginOnline;   // privacy indicator (blue)
    [ObservableProperty] private UiDisplayMode _displayMode = UiDisplayMode.ChatAndCompanion;
    [ObservableProperty] private string _statusText = "local only · сети нет";

    public event Action<ChatMessageModel>? MessageAdded;

    public ConversationViewModel(Orchestrator orchestrator, IChatHistoryStore history, TaskTracker taskTracker)
    {
        _orchestrator = orchestrator;
        _history = history;
        _taskTracker = taskTracker;

        _taskTracker.TaskStarted += t => OnUi(() =>
        {
            IsPluginOnline = true;
            StatusText = "plugin online · " + t.PluginId;
            TaskCards.Add(new TaskCardModel
            {
                Id = t.Id,
                Title = t.PluginId,
                Status = "Работаю над вашим запросом…",
                IsRunning = true
            });
        });

        _taskTracker.TaskProgressed += p => OnUi(() =>
        {
            var card = TaskCards.FirstOrDefault(c => c.Id == p.TaskId);
            if (card is not null)
                card.Status = p.Message;
        });

        _taskTracker.TaskCompleted += (r, t) => OnUi(() =>
        {
            IsPluginOnline = false;
            StatusText = "local only · сети нет";
            var card = TaskCards.FirstOrDefault(c => c.Id == r.TaskId);
            if (card is not null)
            {
                card.IsRunning = false;
                card.IsDone = true;
                card.Status = "Работа над запросом завершена";
                card.Result = r.ResultMarkdown ?? "";
                card.Title = "Готово · " + t.PluginId;
            }
            SaveHistory(HistoryEntryKind.AssistantMessage, r.ResultMarkdown ?? "", "system", t.PluginId);
        });

        _taskTracker.TaskFailed += (r, t) => OnUi(() =>
        {
            IsPluginOnline = false;
            StatusText = "local only · сети нет";
            var card = TaskCards.FirstOrDefault(c => c.Id == r.TaskId);
            if (card is not null)
            {
                card.IsRunning = false;
                card.IsFailed = true;
                card.Status = "Не удалось: " + (r.Error ?? "ошибка");
            }
        });
    }

    /// <summary>Keyboard input lands in the same pipeline as voice (TZ §14.5).</summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
            return;
        InputText = "";

        var msg = new ChatMessageModel { Text = text, IsUser = true, Channel = "⌨" };
        Messages.Add(msg);
        MessageAdded?.Invoke(msg);
        SaveHistory(HistoryEntryKind.UserMessage, text, "keyboard", null);

        // Same routing as a voice question.
        var question = new QuestionReadyEvent(text, "question",
            new Dictionary<string, string>(), 1.0, DateTimeOffset.UtcNow);
        await _orchestrator.HandleQuestionAsync(question);
    }

    /// <summary>Live partial ASR hypothesis as a translucent draft.</summary>
    public void ShowDraft(string text)
    {
        OnUi(() =>
        {
            var draft = Messages.LastOrDefault(m => m.IsDraft);
            if (draft is not null)
                draft.Text = text;
            else
                Messages.Add(new ChatMessageModel { Text = text, IsDraft = true });
        });
    }

    /// <summary>Finalized voice segment replaces the draft.</summary>
    public void FinalizeVoiceMessage(string text)
    {
        OnUi(() =>
        {
            var draft = Messages.LastOrDefault(m => m.IsDraft);
            if (draft is not null)
                Messages.Remove(draft);
            var msg = new ChatMessageModel { Text = text, IsUser = true, Channel = "🎙" };
            Messages.Add(msg);
            MessageAdded?.Invoke(msg);
        });
        SaveHistory(HistoryEntryKind.UserMessage, text, "voice", null);
    }

    [RelayCommand]
    public void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    public void SetDisplayMode(string mode)
    {
        DisplayMode = Enum.Parse<UiDisplayMode>(mode);
    }

    private void SaveHistory(HistoryEntryKind kind, string text, string channel, string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        _ = _history.AppendAsync(new HistoryEntry(0, kind, text, channel, pluginId,
            DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private static void OnUi(Action action)
        => Application.Current?.Dispatcher.Dispatch(action);
}
