using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>One speaker-detection model card on the Models page (Speakers tab); a binding shell over <see cref="DiarizationUiCard"/>.</summary>
public sealed partial class SpeakerModelCardViewModel : ObservableObject
{
    public SpeakerModelCardViewModel(DiarizationCatalogModel model) => Model = model;

    public DiarizationCatalogModel Model { get; }

    public string Title => Model.Title;
    public string Tagline => Model.Tagline;
    public string Description => Model.Description;
    public string SizeChip => DownloadProgressFormatter.FormatBytes(Model.SizeBytes);
    public string PrecisionChip => Model.Precision.ToString().ToUpperInvariant();
    public bool ShowRecommendedChip => Model.IsRecommended;
    public bool ShowDesktopOnlyChip => !Model.PhoneSupported && !OperatingSystem.IsAndroid();

    [ObservableProperty] private bool _showInUseChip;
    [ObservableProperty] private bool _showInstalledChip;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _actionText = "Download";
    [ObservableProperty] private bool _actionVisible = true;
    [ObservableProperty] private bool _isAccentAction = true;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _canDelete;
    [ObservableProperty] private LlmUiAction _action = LlmUiAction.Download;

    public bool ShowAccentAction => ActionVisible && IsAccentAction;
    public bool ShowPlainAction => ActionVisible && !IsAccentAction;
    public string PrimaryAutomationName => $"{ActionText} {Model.Title}";
    public string MoreAutomationName => $"More actions for {Model.Title}";

    public void Apply(DiarizationUiCard card)
    {
        ShowInUseChip = card.Chips.Any(c => c.Kind == LlmUiChipKind.InUse);
        ShowInstalledChip = card.Chips.Any(c => c.Text == "installed");
        StatusText = card.StatusText;
        ActionText = card.ActionText;
        ActionVisible = card.ActionVisible;
        Action = card.Action;
        IsAccentAction = card.Action != LlmUiAction.Cancel;
        ShowProgress = card.ShowProgress;
        Percent = card.Percent;
        ProgressText = card.ProgressText;
        CanDelete = card.CanDelete;
        OnPropertyChanged(nameof(ShowAccentAction));
        OnPropertyChanged(nameof(ShowPlainAction));
        OnPropertyChanged(nameof(PrimaryAutomationName));
    }
}

/// <summary>
/// The Speakers tab of the Models page: status line, one card per model of <see cref="DiarizationModelCatalog.ForDevice"/>
/// (download, cancel, use, delete) and the error banner. Driven by <see cref="IDiarizationModelService"/>, whose
/// <see cref="IDiarizationModelService.Changed"/> may fire on any thread.
/// </summary>
public sealed partial class SpeakerModelsViewModel : ObservableObject
{
    private readonly IDiarizationModelService _service;
    private readonly DispatcherQueue _dispatcher;
    private bool _detached;

    public SpeakerModelsViewModel(IDiarizationModelService service, DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        foreach (var model in service.Models)
            Cards.Add(new SpeakerModelCardViewModel(model));
        _service.Changed += OnServiceChanged;
        Refresh();
    }

    public ObservableCollection<SpeakerModelCardViewModel> Cards { get; } = [];

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _placeFilesText = "";

    public void Refresh()
    {
        foreach (var card in Cards)
        {
            var state = _service.StateOf(card.Model);
            card.Apply(DiarizationUiCardBuilder.Build(card.Model, state, _service.DownloadPercent(card.Model), _service.DownloadProgressText(card.Model)));
        }

        var speakers = SpeakerServices.Current;
        Installed = _service.IsModelAvailable;
        StatusText = SpeakersModelsUi.Status(speakers is not null, Installed, speakers?.ModelStatus);
        PlaceFilesText = SpeakersModelsUi.PlaceFilesText(_service.Layout);
        ErrorText = _service.LastError ?? "";
        HasError = !string.IsNullOrEmpty(ErrorText);
    }

    /// <summary>The card's primary action: Download, Cancel or Use.</summary>
    public async Task RunPrimaryAsync(SpeakerModelCardViewModel card)
    {
        try
        {
            switch (card.Action)
            {
                case LlmUiAction.Download:
                    _service.StartDownload(card.Model);
                    break;
                case LlmUiAction.Cancel:
                    _service.CancelDownload(card.Model);
                    break;
                case LlmUiAction.Use:
                    await _service.SelectAsync(card.Model);
                    break;
            }
        }
        catch (Exception ex)
        {
            ErrorText = $"{card.Title}: {ex.Message}";
            HasError = true;
            return;
        }
        Refresh();
    }

    public void Delete(SpeakerModelCardViewModel card)
    {
        try
        {
            _service.Delete(card.Model);
        }
        catch (Exception ex)
        {
            ErrorText = $"{card.Title}: {ex.Message}";
            HasError = true;
            return;
        }
        Refresh();
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

    private void OnServiceChanged()
    {
        if (!_detached)
            _dispatcher.TryEnqueue(Refresh);
    }
}
