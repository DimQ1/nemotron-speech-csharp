using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>One language-model card on the Models page (the Gemma tab); a thin binding shell over <see cref="LlmUiCard"/>.</summary>
public sealed partial class LlmModelCardViewModel : ObservableObject
{
    private readonly LlmUiDownloadRate _rate = new();

    public LlmModelCardViewModel(LlmModelInfo model)
    {
        Model = model;
    }

    public LlmModelInfo Model { get; }

    public string Title => Model.Title;
    public string Tagline => Model.Tagline;
    public string Description => Model.Description;
    public string SizeChip => DownloadProgressFormatter.FormatBytes(Model.SizeBytes);
    public string ContextChip => $"{LlmUiCardBuilder.FormatContext(Model.ContextTokens)} context";
    public bool ShowRecommendedChip { get; private set; }
    public bool ShowDesktopOnlyChip { get; private set; }

    [ObservableProperty] private bool _showInUseChip;
    [ObservableProperty] private bool _showInstalledChip;
    [ObservableProperty] private bool _showLowMemoryChip;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _ramWarningText = "";
    [ObservableProperty] private string _actionText = "Download";
    [ObservableProperty] private bool _actionVisible = true;
    [ObservableProperty] private bool _actionEnabled = true;
    [ObservableProperty] private bool _isAccentAction = true;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private LlmUiAction _action = LlmUiAction.Download;

    public bool ShowAccentAction => ActionVisible && IsAccentAction;
    public bool ShowPlainAction => ActionVisible && !IsAccentAction;
    public string PrimaryAutomationName => $"{ActionText} {Model.Title}";
    public string MoreAutomationName => $"More actions for {Model.Title}";

    public void Apply(LlmUiCard card, double? availableGb, DateTime now)
    {
        ShowRecommendedChip = card.Chips.Any(c => c.Kind == LlmUiChipKind.Recommended);
        ShowDesktopOnlyChip = card.Chips.Any(c => c.Text == "Desktop only");
        OnPropertyChanged(nameof(ShowRecommendedChip));
        OnPropertyChanged(nameof(ShowDesktopOnlyChip));

        ShowInUseChip = card.Chips.Any(c => c.Kind == LlmUiChipKind.InUse);
        ShowInstalledChip = card.Chips.Any(c => c.Text == "installed");
        ShowLowMemoryChip = card.RamLow;
        StatusText = card.StatusText;
        RamText = card.RamText;
        RamWarningText = card.RamLow && availableGb is { } free
            ? $"Only {free:0.#} GB free now. Close other apps first or loading may fail."
            : "";
        ActionText = card.ActionText;
        ActionVisible = card.ActionVisible;
        ActionEnabled = card.ActionEnabled;
        Action = card.Action;
        IsAccentAction = card.Action != LlmUiAction.Cancel;
        ShowProgress = card.ShowProgress;
        Percent = card.Percent;
        CanDelete = card.CanDelete;
        IsActive = card.State == LlmModelState.Active;
        OnPropertyChanged(nameof(ShowAccentAction));
        OnPropertyChanged(nameof(ShowPlainAction));
        OnPropertyChanged(nameof(PrimaryAutomationName));

        if (card.ShowProgress)
        {
            var bps = _rate.Update(card.Percent, Model.SizeBytes, now);
            var downloaded = (long)(card.Percent / 100d * Model.SizeBytes);
            RateText = ModelsPageFormatter.DescribeProgress(card.Percent, downloaded, Model.SizeBytes)
                + (bps > 0 ? " · " + ModelsPageFormatter.DescribeRate(bps, downloaded, Model.SizeBytes) : "");
        }
        else
        {
            _rate.Reset();
            RateText = "";
        }
    }
}

/// <summary>
/// The Gemma tab of the Models page: one card per model of <see cref="LlmModelCatalog.ForDevice"/>,
/// the CPU/GPU choice and the switch progress / error banner. Everything is driven by
/// <see cref="ILlmModelService"/>; its <see cref="ILlmModelService.Changed"/> may fire on any thread.
/// </summary>
public sealed partial class LlmModelsViewModel : ObservableObject
{
    private readonly ILlmModelService _service;
    private readonly DispatcherQueue _dispatcher;
    private readonly bool _isPhone;
    private string? _loadingModelId;
    private bool _detached;

    public LlmModelsViewModel(ILlmModelService service, DispatcherQueue dispatcher, bool isPhone)
    {
        _service = service;
        _dispatcher = dispatcher;
        _isPhone = isPhone;

        foreach (var model in LlmUiCardBuilder.ModelsFor(isPhone))
            Cards.Add(new LlmModelCardViewModel(model));

        _service.Changed += OnServiceChanged;
        Refresh();
    }

    public ObservableCollection<LlmModelCardViewModel> Cards { get; } = new();

    /// <summary>The compute choice exists on desktop only; phones always run the CPU.</summary>
    public bool IsComputeChoiceAvailable => !_isPhone;

    public string ComputeHint => IsComputeChoiceAvailable ? LlmUiText.ComputeHint : LlmUiText.ComputeUnavailableHint;

    [ObservableProperty] private bool _isGpu;
    [ObservableProperty] private bool _isCpu = true;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _isComputeEnabled = true;

    public void Refresh()
    {
        var available = DeviceMemory.AvailableGb;
        var now = DateTime.UtcNow;
        foreach (var card in Cards)
        {
            var state = _service.StateOf(card.Model);
            var ui = LlmUiCardBuilder.Build(card.Model, state, _service.DownloadPercent(card.Model),
                available, _loadingModelId, isPhone: _isPhone);
            card.Apply(ui, available, now);
        }

        var gpu = string.Equals(_service.ComputeBackend, "gpu", StringComparison.OrdinalIgnoreCase);
        IsGpu = gpu;
        IsCpu = !gpu;
        IsComputeEnabled = !IsLoading;
    }

    public void StartDownload(LlmModelCardViewModel card)
    {
        ClearError();
        Guard(() => _service.StartDownload(card.Model), card.Model);
        Refresh();
    }

    public void CancelDownload(LlmModelCardViewModel card)
    {
        Guard(() => _service.CancelDownload(card.Model), card.Model);
        Refresh();
    }

    public void Delete(LlmModelCardViewModel card)
    {
        ClearError();
        Guard(() => _service.Delete(card.Model), card.Model);
        Refresh();
    }

    /// <summary>The card's primary action: Download, Cancel or Use.</summary>
    public async Task RunPrimaryAsync(LlmModelCardViewModel card)
    {
        switch (card.Action)
        {
            case LlmUiAction.Download:
                StartDownload(card);
                break;
            case LlmUiAction.Cancel:
                CancelDownload(card);
                break;
            case LlmUiAction.Use:
                await UseAsync(card);
                break;
        }
    }

    public async Task UseAsync(LlmModelCardViewModel card)
    {
        if (IsLoading)
            return;

        await RunLoadingAsync(card.Model.Id, LlmUiText.Loading(card.Model.Title),
            () => _service.SelectAsync(card.Model), card.Model);
    }

    public async Task SetComputeAsync(string backend)
    {
        if (IsLoading || string.Equals(_service.ComputeBackend, backend, StringComparison.OrdinalIgnoreCase))
            return;

        var label = backend.Equals("gpu", StringComparison.OrdinalIgnoreCase) ? "GPU" : "CPU";
        await RunLoadingAsync(null, $"Switching to {label}…", () => _service.SetComputeBackendAsync(backend), _service.Active);
    }

    public void ClearError()
    {
        HasError = false;
        ErrorText = "";
    }

    public void Detach()
    {
        _detached = true;
        _service.Changed -= OnServiceChanged;
    }

    private async Task RunLoadingAsync(string? modelId, string text, Func<Task> work, LlmModelInfo forErrors)
    {
        ClearError();
        _loadingModelId = modelId ?? "";
        LoadingText = text;
        IsLoading = true;
        Refresh();
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // The user left; nothing to report.
        }
        catch (Exception ex)
        {
            ErrorText = LlmUiText.SwitchError(forErrors, ex);
            HasError = true;
        }
        finally
        {
            _loadingModelId = null;
            IsLoading = false;
            Refresh();
        }
    }

    private void Guard(Action action, LlmModelInfo model)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorText = $"{model.Title}: {ex.Message}";
            HasError = true;
        }
    }

    private void OnServiceChanged()
    {
        if (!_detached)
            _dispatcher.TryEnqueue(Refresh);
    }
}
