using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using SpeechLib.ModelFormats;
using VoiceType.Uno.Localization;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// ViewModel of the Speech tab of the Models page: every downloadable ASR model as
/// a card with its measured accuracy/speed/size, which of them is installed and
/// which one is currently in use. Owns the download/use/delete actions for the cards.
/// </summary>
public sealed partial class ModelManagerViewModel : ObservableObject
{
    private readonly ModelDownloadManager _downloads;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<AppSettings, Task> _applySettings;

    public ModelManagerViewModel(
        ModelDownloadManager downloads,
        DispatcherQueue dispatcher,
        Func<AppSettings> settingsProvider,
        Func<AppSettings, Task> applySettings)
    {
        _downloads = downloads;
        _dispatcher = dispatcher;
        _settingsProvider = settingsProvider;
        _applySettings = applySettings;

        Ranking = ModelCatalog.Ranking;
        foreach (var model in ModelCatalog.OrderedModels)
            Cards.Add(new ModelCardViewModel(model, Ranking, this, dispatcher));

        Refresh();

        // Downloads keep running when the page closes; reopening it shows their progress.
        foreach (var card in Cards)
            card.AttachToActiveJob();
    }

    public ModelRankingTable Ranking { get; }

    /// <summary>Catalog entries in display order (recommended first).</summary>
    public ObservableCollection<ModelCardViewModel> Cards { get; } = [];

    /// <summary>The model storage folder (parent of <c>asr/</c>, <c>llm/</c> and <c>diarization/</c>).</summary>
    public string ModelsRootPath { get; private set; } = "";

    /// <summary>Where speech models are stored and downloaded: <c>&lt;root&gt;/asr</c>.</summary>
    public string AsrDir => Layout.AsrDir;

    private ModelStorageLayout Layout => new(ModelsRootPath);

    public string StorageText => Loc.F("Models.Storage.StoredAndPlace",
        ModelStorageText.StoredIn(ModelKind.Speech, Layout), ModelStorageText.PlaceFilesText(ModelKind.Speech, Layout));

    /// <summary>Friendly name of the model the app currently uses.</summary>
    public string ActiveModelText
    {
        get
        {
            var folder = ActiveFolder;
            if (string.IsNullOrWhiteSpace(folder))
                return Loc.T("Models.Speech.NoModelSelected");

            return ModelCatalog.FindBySubfolder(folder) is { } known
                ? Loc.F("Models.Speech.InUseKnown", ModelCatalogText.Title(known), folder)
                : Loc.F("Models.Speech.InUseLocal", folder);
        }
    }

    /// <summary>Catalog title of the model in use, or the raw folder name for custom models.</summary>
    public string ActiveModelTitle => DefaultAsrModel.Describe(ActiveFolder);

    public string InstalledText => InstalledCount == 0
        ? Loc.T("Models.Speech.NoneDownloaded")
        : Loc.Plural("Models.Speech.Downloaded", InstalledCount, Cards.Count);

    /// <summary>Answers "which should I download?" with the catalog recommendation.</summary>
    public string RecommendationText => Loc.F("Models.Speech.Recommendation",
        ModelCatalogText.Title(ModelCatalog.Recommended),
        ModelCatalogText.Tagline(ModelCatalog.Recommended).ToLower(Loc.Culture),
        ModelCatalog.Recommended.SizeText,
        ModelCatalogText.Speed(ModelCatalog.Recommended));

    public string AccuracyLeaderText => Ranking.MostAccurate is { } model
        ? Loc.F("Models.Speech.MostAccurateLeader", ModelCatalogText.Title(model), ModelCatalogText.Accuracy(model), ModelCatalogText.AccuracyDetail(model))
        : Loc.T("Models.Speech.AccuracyNotMeasured");

    public string SpeedLeaderText => Ranking.Fastest is { } model
        ? Loc.F("Models.Speech.FastestLeader", ModelCatalogText.Title(model), ModelCatalogText.Speed(model))
        : Loc.T("Models.Speech.SpeedNotMeasured");

    public string SizeLeaderText => Ranking.Smallest is { } model
        ? Loc.F("Models.Speech.SmallestLeader", ModelCatalogText.Title(model), model.SizeText)
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusText = "";

    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText))]
    private int _installedCount;

    /// <summary>Page subtitle over every installed model (catalog + translation), e.g. "2 installed · 1.4 GB used".</summary>
    [ObservableProperty]
    private string _installedSummaryText = "";

    /// <summary>Folder configured in settings; empty means the default models root.</summary>
    private string ActiveFolder => _settingsProvider().SelectedModel ?? "";

    private string ResolveRoot()
    {
        return ModelStorageLayout.Resolve(_settingsProvider()).Root;
    }

    /// <summary>Folder of a catalog model: <c>asr/&lt;name&gt;</c>, or the flat root of older versions until it is migrated.</summary>
    private string FolderOf(string folderName) => ModelPathResolver.LocateModelFolder(ModelsRootPath, folderName);

    /// <summary>Re-checks which models are on disk and which one is active.</summary>
    public void Refresh()
    {
        ModelsRootPath = ResolveRoot();
        var active = ActiveFolder;

        foreach (var card in Cards)
        {
            var installed = ModelFolderScanner.IsModelDirectory(FolderOf(card.FolderName));
            var inUse = installed && string.Equals(card.FolderName, active, StringComparison.OrdinalIgnoreCase);
            card.ApplyState(installed, inUse);
        }

        InstalledCount = Cards.Count(card => card.IsInstalled);
        InstalledSummaryText = DescribeInstalled();

        OnPropertyChanged(nameof(ModelsRootPath));
        OnPropertyChanged(nameof(StorageText));
        OnPropertyChanged(nameof(ActiveModelText));
        OnPropertyChanged(nameof(ActiveModelTitle));
        OnPropertyChanged(nameof(RecommendationText));
        OnPropertyChanged(nameof(AccuracyLeaderText));
        OnPropertyChanged(nameof(SpeedLeaderText));
        OnPropertyChanged(nameof(SizeLeaderText));
    }

    /// <summary>
    /// Counts every installed model of the storage folder (speech, language, speaker detection, including folders the
    /// user copied in) and adds up their size on disk (a handful of large files each, so the walk is cheap).
    /// </summary>
    private string DescribeInstalled()
    {
        var summary = ModelStorageScanner.Summarize(Layout);
        return ModelsPageFormatter.DescribeInstalled(summary.TotalCount, summary.TotalBytes);
    }

    /// <summary>Stops the cards' progress updates when the page closes; downloads keep running.</summary>
    public void Detach()
    {
        foreach (var card in Cards)
            card.Detach();
    }

    /// <summary>Queues a catalog variant into the shared download manager.</summary>
    internal DownloadJob EnqueueDownload(ModelDescriptor model, bool force)
    {
        // The manager de-duplicates and resumes; "re-download" means starting from
        // an empty folder, so the caller removes it first.
        if (force)
            DeleteModelFolder(model.SubfolderName);

        return _downloads.Enqueue(ModelDownloadRequest.ForCatalogModel(model, AsrDir));
    }

    /// <summary>The download pipeline, so cards can observe and cancel their own job.</summary>
    internal ModelDownloadManager Downloads => _downloads;

    private void DeleteModelFolder(string folderName)
    {
        try
        {
            var folder = FolderOf(folderName);
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = Loc.F("Models.Speech.CouldNotClear", folderName, ex.Message);
        }
    }

    /// <summary>
    /// A downloaded model becomes the active one: the user asked for this card,
    /// so switching to it is the expected outcome of pressing Download.
    /// </summary>
    internal async Task OnDownloadCompletedAsync(ModelCardViewModel card)
    {
        void RefreshOnUi() => Refresh();
        _dispatcher.TryEnqueue(RefreshOnUi);

        if (card.IsInUse)
            return;

        await UseAsync(card).ConfigureAwait(false);
    }

    /// <summary>Switches the app to a model that is already on disk.</summary>
    internal async Task UseAsync(ModelCardViewModel card)
    {
        if (!card.IsInstalled)
            return;

        var settings = _settingsProvider();
        settings.ModelsRootPath = ModelsRootPath;
        settings.SelectedModel = card.FolderName;
        settings.ModelPath = FolderOf(card.FolderName);

        _dispatcher.TryEnqueue(() => StatusText = Loc.F("Models.Speech.Switching", card.Title));
        await _applySettings(settings).ConfigureAwait(false);
        _dispatcher.TryEnqueue(() =>
        {
            StatusText = Loc.F("Models.Speech.NowUsing", card.Title);
            Refresh();
        });
    }

    /// <summary>Deletes a downloaded model folder (the active one is protected by the card's CanDelete).</summary>
    internal void Delete(ModelCardViewModel card)
    {
        try
        {
            var path = FolderOf(card.FolderName);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);

            StatusText = Loc.F("Models.Speech.Deleted", card.Title);
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("Models.Error.CouldNotDelete", card.Title, ex.Message);
        }

        Refresh();
    }

    /// <summary>Cancels every queued/running download.</summary>
    public void CancelAll()
    {
        _downloads.CancelAll();
        _dispatcher.TryEnqueue(() => StatusText = Loc.T("Models.Speech.CancelledAll"));
    }
}
