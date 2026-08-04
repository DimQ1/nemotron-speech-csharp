using System.Collections.ObjectModel;
using LocalVoiceAssistant.Maui.Core;
using LocalVoiceAssistant.Maui.Models;
using LocalVoiceAssistant.Maui.Plugins;

namespace LocalVoiceAssistant.Maui.Services;

public sealed class VoiceAssistantOrchestrator : IDisposable
{
    private readonly ISpeechRecognitionService _recognitionService;
    private readonly IPluginHost _pluginHost;
    private readonly IActivationGate _activationGate;
    private readonly IConversationStore _conversationStore;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly ObservableCollection<ConversationEntry> _context = new();
    private bool _disposed;

    public VoiceAssistantOrchestrator(
        ISpeechRecognitionService recognitionService,
        IPluginHost pluginHost,
        IActivationGate activationGate,
        IConversationStore conversationStore)
    {
        _recognitionService = recognitionService;
        _pluginHost = pluginHost;
        _activationGate = activationGate;
        _conversationStore = conversationStore;

        _recognitionService.TranscriptReceived += OnTranscriptReceived;
        _recognitionService.StatusChanged += OnStatusChanged;
        _recognitionService.ErrorOccurred += OnErrorOccurred;
    }

    public RuntimeSnapshot Snapshot { get; private set; } =
        new(RuntimeState.Idle, "Готов", null, null, null);

    public IReadOnlyList<IAssistantPlugin> Plugins => _pluginHost.Plugins;
    public IReadOnlyList<ConversationEntry> Context => _context;

    public event EventHandler<string>? TranscriptReceived;
    public event EventHandler<string>? StatusReceived;
    public event EventHandler<string>? ErrorReceived;
    public event EventHandler<AssistantResponse>? ActionHandled;
    public event EventHandler<ConversationEntry>? ConversationReceived;
    public event EventHandler<RuntimeSnapshot>? RuntimeChanged;
    public event EventHandler<PluginProgress>? ProgressChanged;
    public event EventHandler? QuestionReady;

    public Task StartAsync(
        string modelPath,
        string language,
        string executionProvider,
        CancellationToken cancellationToken) =>
        _recognitionService.StartAsync(modelPath, language, executionProvider, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _recognitionService.StopAsync(cancellationToken);

    public async Task<AssistantResponse> ProcessTextAsync(
        string text,
        bool bypassActivation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new AssistantResponse("Пустая команда.", IsWarning: true);

        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var input = text.Trim();
            await AddConversationAsync(new ConversationEntry(
                Guid.NewGuid(),
                "user",
                input,
                DateTimeOffset.Now), cancellationToken).ConfigureAwait(false);
            TranscriptReceived?.Invoke(this, input);

            var activation = bypassActivation
                ? new ActivationDecision(true, 1, true, input, null)
                : _activationGate.Evaluate(input);

            if (!activation.IsActivated)
            {
                SetSnapshot(new RuntimeSnapshot(
                    RuntimeState.WaitingForQuestion,
                    "Ожидаю обращение «Ассистент»",
                    null,
                    null,
                    "Семантическая активация не пройдена."));
                QuestionReady?.Invoke(this, EventArgs.Empty);
                return new AssistantResponse(
                    "Я слушаю. Начните команду со слова «Ассистент».",
                    IsWarning: true);
            }

            var plugin = _pluginHost.Plugins.FirstOrDefault(candidate => candidate.CanHandle(activation.CleanText));
            if (plugin is null)
            {
                var unknown = new AssistantResponse(
                    "Команда активирована, но подходящий плагин не найден. Скажите «Ассистент, помощь».",
                    IsWarning: true);
                await PublishResultAsync(unknown, null, cancellationToken).ConfigureAwait(false);
                return unknown;
            }

            SetSnapshot(new RuntimeSnapshot(
                RuntimeState.Executing,
                $"Выполняю «{plugin.Manifest.Name}»",
                plugin.Manifest.Id,
                0,
                "Подготавливаю вызов инструмента."));

            var progress = new Progress<PluginProgress>(value =>
            {
                ProgressChanged?.Invoke(this, value);
                SetSnapshot(new RuntimeSnapshot(
                    RuntimeState.Executing,
                    value.Message,
                    plugin.Manifest.Id,
                    value.Percent,
                    value.Message));
            });
            var result = await _pluginHost.ExecuteAsync(
                new ToolInvocation(plugin.Manifest.Id, activation.CleanText, DateTimeOffset.UtcNow),
                progress,
                cancellationToken).ConfigureAwait(false);
            var response = result.ToAssistantResponse();
            await PublishResultAsync(response, plugin.Manifest.Id, cancellationToken).ConfigureAwait(false);

            if (response.ShouldStop)
                await StopAsync(CancellationToken.None).ConfigureAwait(false);

            return response;
        }
        finally
        {
            _executionGate.Release();
        }
    }

    public Task<IReadOnlyList<ConversationEntry>> SearchHistoryAsync(
        string query,
        CancellationToken cancellationToken) =>
        _conversationStore.SearchAsync(query, cancellationToken);

    private async void OnTranscriptReceived(object? sender, string transcript)
    {
        try
        {
            await ProcessTextAsync(transcript, false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OnErrorOccurred(this, ex.Message);
        }
    }

    private void OnStatusChanged(object? sender, string status)
    {
        SetSnapshot(new RuntimeSnapshot(
            _recognitionService.IsSupported ? RuntimeState.Listening : RuntimeState.Error,
            status,
            Snapshot.ActivePluginId,
            Snapshot.Progress,
            Snapshot.ProgressText));
        StatusReceived?.Invoke(this, status);
    }

    private void OnErrorOccurred(object? sender, string error)
    {
        SetSnapshot(new RuntimeSnapshot(RuntimeState.Error, error, null, null, null));
        ErrorReceived?.Invoke(this, error);
    }

    private async Task PublishResultAsync(
        AssistantResponse response,
        string? pluginId,
        CancellationToken cancellationToken)
    {
        var entry = new ConversationEntry(
            Guid.NewGuid(),
            "assistant",
            response.Message,
            DateTimeOffset.Now,
            pluginId,
            response.IsWarning);
        await AddConversationAsync(entry, cancellationToken).ConfigureAwait(false);
        ActionHandled?.Invoke(this, response);
        SetSnapshot(new RuntimeSnapshot(
            response.IsWarning ? RuntimeState.Error : RuntimeState.Idle,
            response.Message,
            pluginId,
            response.IsWarning ? null : 1,
            response.IsWarning ? "Требуется уточнение." : "Готово."));
    }

    private async Task AddConversationAsync(ConversationEntry entry, CancellationToken cancellationToken)
    {
        _context.Add(entry);
        while (_context.Count > 20)
            _context.RemoveAt(0);
        ConversationReceived?.Invoke(this, entry);
        await _conversationStore.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private void SetSnapshot(RuntimeSnapshot snapshot)
    {
        Snapshot = snapshot;
        RuntimeChanged?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _recognitionService.TranscriptReceived -= OnTranscriptReceived;
        _recognitionService.StatusChanged -= OnStatusChanged;
        _recognitionService.ErrorOccurred -= OnErrorOccurred;
        _recognitionService.Dispose();
        _executionGate.Dispose();
    }
}
