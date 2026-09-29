using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>
/// ViewModel of the model manager: every downloadable ASR model as a card with
/// its measured accuracy/speed/size, which of them is installed and which one is
/// currently in use. Owns the download/use/delete actions for the cards.
/// </summary>
public sealed partial class ModelManagerViewModel : ObservableObject
{
    private readonly DownloadQueueService _queue;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<AppSettings, Task> _applySettings;

    public ModelManagerViewModel(
        DownloadQueueService queue,
        DispatcherQueue dispatcher,
        Func<AppSettings> settingsProvider,
        Func<AppSettings, Task> applySettings)
    {
        _queue = queue;
        _dispatcher = dispatcher;
        _settingsProvider = settingsProvider;
        _applySettings = applySettings;

        Ranking = ModelCatalog.Ranking;
        foreach (var model in ModelCatalog.OrderedModels)
            Cards.Add(new ModelCardViewModel(model, Ranking, this, dispatcher));

        Refresh();
    }

    public ModelRankingTable Ranking { get; }

    /// <summary>Catalog entries in display order (recommended first).</summary>
    public ObservableCollection<ModelCardViewModel> Cards { get; } = [];

    /// <summary>Folder the models are downloaded into.</summary>
    public string ModelsRootPath { get; private set; } = "";

    public string StorageText => $"Models are stored in {ModelsRootPath}";

    /// <summary>Friendly name of the model the app currently uses.</summary>
    public string ActiveModelText
    {
        get
        {
            var folder = ActiveFolder;
            if (string.IsNullOrWhiteSpace(folder))
                return "No model selected yet";

            return ModelCatalog.FindBySubfolder(folder) is { } known
                ? $"In use: {known.Title} ({folder})"
                : $"In use: {folder} — a local model that is not one of the downloads below";
        }
    }

    /// <summary>Catalog title of the model in use, or the raw folder name for custom models.</summary>
    public string ActiveModelTitle => ModelCatalog.DescribeFolder(ActiveFolder);

    public string InstalledText => InstalledCount == 0
        ? "No catalog model downloaded yet — the recommended one is a safe default."
        : $"{InstalledCount} of {Cards.Count} catalog models downloaded";

    /// <summary>Answers "which should I download?" with the catalog recommendation.</summary>
    public string RecommendationText =>
        $"Start with {ModelCatalog.Recommended.Title} — {ModelCatalog.Recommended.Tagline.ToLowerInvariant()}, " +
        $"{ModelCatalog.Recommended.SizeText} download, {ModelCatalog.Recommended.SpeedText}.";

    public string AccuracyLeaderText => Ranking.MostAccurate is { } model
        ? $"Most accurate: {model.Title} — {model.AccuracyText} ({model.AccuracyDetailText})"
        : "Accuracy has not been measured for these models yet.";

    public string SpeedLeaderText => Ranking.Fastest is { } model
        ? $"Fastest: {model.Title} — {model.SpeedText}"
        : "Speed has not been measured for these models yet.";

    public string SizeLeaderText => Ranking.Smallest is { } model
        ? $"Smallest download: {model.Title} — {model.SizeText}"
        : "";

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText))]
    private int _installedCount;

    /// <summary>Folder configured in settings; empty means the default models root.</summary>
    private string ActiveFolder => _settingsProvider().SelectedModel ?? "";

    private string ResolveRoot()
    {
        var configured = _settingsProvider().ModelsRootPath;
        return string.IsNullOrWhiteSpace(configured) ? AppPaths.ModelsDir : configured;
    }

    /// <summary>Re-checks which models are on disk and which one is active.</summary>
    public void Refresh()
    {
        ModelsRootPath = ResolveRoot();
        var active = ActiveFolder;

        foreach (var card in Cards)
        {
            var installed = ModelFolderScanner.IsModelDirectory(Path.Combine(ModelsRootPath, card.FolderName));
            var inUse = installed && string.Equals(card.FolderName, active, StringComparison.OrdinalIgnoreCase);
            card.ApplyState(installed, inUse);
        }

        InstalledCount = Cards.Count(card => card.IsInstalled);

        OnPropertyChanged(nameof(ModelsRootPath));
        OnPropertyChanged(nameof(StorageText));
        OnPropertyChanged(nameof(ActiveModelText));
        OnPropertyChanged(nameof(ActiveModelTitle));
        OnPropertyChanged(nameof(RecommendationText));
        OnPropertyChanged(nameof(AccuracyLeaderText));
        OnPropertyChanged(nameof(SpeedLeaderText));
        OnPropertyChanged(nameof(SizeLeaderText));
    }

    /// <summary>Queues a catalog variant into the shared parallel download queue.</summary>
    internal DownloadQueueItem EnqueueDownload(ModelDescriptor model, bool force)
        => _queue.EnqueueAsrModel(
            ModelsRootPath,
            _ => { },
            forceRedownload: force,
            repoId: model.RepoId,
            quantizationFolder: model.QuantizationFolder);

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
        settings.ModelPath = Path.Combine(ModelsRootPath, card.FolderName);

        _dispatcher.TryEnqueue(() => StatusText = $"Switching to {card.Title}...");
        await _applySettings(settings).ConfigureAwait(false);
        _dispatcher.TryEnqueue(() =>
        {
            StatusText = $"Now using {card.Title}";
            Refresh();
        });
    }

    /// <summary>Deletes a downloaded model folder (the active one is protected by the card's CanDelete).</summary>
    internal void Delete(ModelCardViewModel card)
    {
        try
        {
            var path = Path.Combine(ModelsRootPath, card.FolderName);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);

            StatusText = $"Deleted {card.Title}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not delete {card.Title}: {ex.Message}";
        }

        Refresh();
    }

    /// <summary>Cancels every queued/running download.</summary>
    public void CancelAll()
    {
        _queue.CancelAll();
        _dispatcher.TryEnqueue(() => StatusText = "Cancelled all downloads");
    }
}
