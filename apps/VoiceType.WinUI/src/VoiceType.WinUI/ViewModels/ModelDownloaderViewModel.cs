using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SpeechLib.ModelDownload;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

/// <summary>
/// Download manager window: the model catalog (each card with its own Download
/// button, so several models download at once), the list of downloads with
/// per-model and overall progress, and a notice for every finished model.
/// The downloads themselves live in the app-wide <see cref="DownloadCenter"/>,
/// so closing the window does not stop them.
/// </summary>
public sealed partial class ModelDownloaderViewModel : ObservableObject, IDisposable
{
    private readonly DownloadCenter _center;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher;
    private readonly Dictionary<Guid, DownloadJobViewModel> _rows = new();
    private bool _disposed;

    public nint OwnerWindowHandle { get; set; }

    public ModelDownloaderViewModel(DownloadCenter center, ISettingsService settingsService, DispatcherQueue dispatcher)
    {
        _center = center;
        _settingsService = settingsService;
        _dispatcher = dispatcher;

        ModelsRootPath = ResolveModelsRootPath(settingsService.Load());
        SelectedUseCase = UseCaseOptions[0];

        foreach (var job in _center.Manager.Jobs)
            AddRow(job);

        _center.Manager.JobAdded += OnJobAdded;
        _center.Manager.JobUpdated += OnJobUpdated;
        _center.Manager.JobRemoved += OnJobRemoved;
        _center.NoticeRaised += OnNotice;

        RefreshCards();
        RefreshTotals();
    }

    // ---- Catalog ----

    public IReadOnlyList<ModelCardViewModel> Models { get; } =
        ModelCatalog.Models.Select(m => new ModelCardViewModel(m)).ToList();

    public IReadOnlyList<ModelUseCaseOption> UseCaseOptions { get; } =
    [
        new ModelUseCaseOption("All models", null),
        new ModelUseCaseOption("Best accuracy — Parakeet, 25 languages, live text", ModelUseCase.Multilingual),
        new ModelUseCaseOption("Fast dictation — Nemotron, lowest CPU load", ModelUseCase.FastDictation),
        new ModelUseCaseOption("Higher quality — Nemotron / Qwen3", ModelUseCase.HighQuality),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredModels))]
    private ModelUseCaseOption? _selectedUseCase;

    public IReadOnlyList<ModelCardViewModel> FilteredModels =>
        Models.Where(m => SelectedUseCase?.UseCase is not { } useCase || m.Descriptor.UseCase == useCase).ToList();

    [ObservableProperty]
    private string _modelsRootPath = string.Empty;

    partial void OnModelsRootPathChanged(string value) => RefreshCards();

    // ---- Downloads ----

    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = new();

    public ObservableCollection<DownloadNoticeViewModel> Notices { get; } = new();

    [ObservableProperty] private double _totalPercent;
    [ObservableProperty] private string _totalDetail = "No downloads yet";
    [ObservableProperty] private bool _hasActiveJobs;
    [ObservableProperty] private bool _hasFinishedJobs;

    public Visibility EmptyVisibility => Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Models root to hand back to Settings after a download (null when nothing finished here).</summary>
    public string? ResultPath { get; private set; }
    public bool WasDownloaded => ResultPath is not null;

    [RelayCommand]
    private void DownloadModel(ModelCardViewModel? card)
    {
        if (card is null || string.IsNullOrWhiteSpace(ModelsRootPath))
            return;

        Directory.CreateDirectory(ModelsRootPath);
        _center.EnqueueCatalogModel(card.Descriptor, ModelsRootPath);
        PersistModelsRoot();
    }

    [RelayCommand]
    private void DownloadRecommended() =>
        DownloadModel(Models.FirstOrDefault(m => m.IsRecommended));

    [RelayCommand]
    private void DownloadTranslationModel() => _center.EnqueueTranslationModel();

    [RelayCommand]
    private void Cancel(DownloadJobViewModel? row)
    {
        if (row is not null)
            _center.Manager.Cancel(row.Job);
    }

    [RelayCommand]
    private void CancelAll() => _center.Manager.CancelAll();

    [RelayCommand]
    private void Retry(DownloadJobViewModel? row)
    {
        if (row is not null)
            _center.Manager.Retry(row.Job);
    }

    [RelayCommand]
    private void ClearFinished() => _center.Manager.ClearFinished();

    /// <summary>Switches recognition to a downloaded model.</summary>
    [RelayCommand]
    private void UseModel(DownloadJobViewModel? row)
    {
        if (row?.Job.ResultPath is not { } path)
            return;
        WeakReferenceMessenger.Default.Send(new UseModelMessage(path));
    }

    [RelayCommand]
    private void DismissNotice(DownloadNoticeViewModel? notice)
    {
        if (notice is not null)
            Notices.Remove(notice);
    }

    [RelayCommand]
    private async Task BrowseRoot()
    {
        var initialPath = Directory.Exists(ModelsRootPath) ? ModelsRootPath : AppPaths.DataRoot;
        var path = await FolderBrowser.ShowAsync("Select root folder for downloaded models", initialPath, OwnerWindowHandle);
        if (path is not null)
            ModelsRootPath = path;
    }

    // ---- Manager events (worker threads → UI thread) ----

    private void OnJobAdded(DownloadJob job) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        AddRow(job);
        RefreshCards();
        RefreshTotals();
    });

    private void OnJobUpdated(DownloadJob job) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        if (_rows.TryGetValue(job.Id, out var row))
            row.Refresh();
        UpdateCard(job);
        RefreshTotals();
    });

    private void OnJobRemoved(DownloadJob job) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        if (_rows.Remove(job.Id, out var row))
            Jobs.Remove(row);
        OnPropertyChanged(nameof(EmptyVisibility));
        RefreshCards();
        RefreshTotals();
    });

    private void OnNotice(DownloadNotice notice) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        Notices.Insert(0, new DownloadNoticeViewModel(notice));
        while (Notices.Count > 6)
            Notices.RemoveAt(Notices.Count - 1);

        if (notice.Success && notice.Job.Request.Kind == ModelDownloadKind.Recognition)
            ResultPath = ModelsRootPath;
        RefreshCards();
    });

    private void AddRow(DownloadJob job)
    {
        if (_rows.ContainsKey(job.Id))
            return;
        var row = new DownloadJobViewModel(job);
        _rows[job.Id] = row;
        Jobs.Insert(0, row);
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private void UpdateCard(DownloadJob job)
    {
        var card = Models.FirstOrDefault(m => string.Equals(m.Descriptor.SubfolderName, job.Key, StringComparison.OrdinalIgnoreCase));
        if (card is null)
            return;
        card.IsDownloading = job.IsActive;
        card.Progress = job.Percent;
        if (job.State == DownloadJobState.Completed)
            card.IsInstalled = true;
    }

    private void RefreshCards()
    {
        foreach (var card in Models)
        {
            var active = _center.Manager.FindActive(card.Descriptor.SubfolderName);
            card.IsDownloading = active is not null;
            card.Progress = active?.Percent ?? 0;
            card.IsInstalled = !string.IsNullOrWhiteSpace(ModelsRootPath) && DownloadCenter.IsInstalled(card.Descriptor, ModelsRootPath);
        }
    }

    private void RefreshTotals()
    {
        var totals = _center.Manager.Totals;
        TotalPercent = totals.Percent;
        HasActiveJobs = totals.Active > 0;
        HasFinishedJobs = Jobs.Any(j => !j.IsActive);

        if (Jobs.Count == 0)
        {
            TotalDetail = "No downloads yet";
            return;
        }

        var parts = new List<string>
        {
            totals.Active > 0 ? $"{totals.Active} downloading" : "All downloads finished",
            $"{totals.Completed} completed",
        };
        if (totals.Failed > 0)
            parts.Add($"{totals.Failed} failed");
        if (totals.TotalBytes > 0)
            parts.Add($"{DownloadJobViewModel.Format(totals.DownloadedBytes)} / {DownloadJobViewModel.Format(totals.TotalBytes)} ({totals.Percent:F0}%)");
        if (totals.BytesPerSecond > 0)
        {
            parts.Add($"{DownloadJobViewModel.Format((long)totals.BytesPerSecond)}/s");
            var remaining = totals.TotalBytes - totals.DownloadedBytes;
            if (remaining > 0)
                parts.Add(DownloadJobViewModel.FormatEta(TimeSpan.FromSeconds(remaining / totals.BytesPerSecond)));
        }
        TotalDetail = string.Join(" · ", parts);
    }

    private void PersistModelsRoot()
    {
        var root = ModelsRootPath;
        _ = Task.Run(() => _settingsService.Update(settings =>
        {
            if (string.IsNullOrWhiteSpace(settings.ModelsRootPath))
                settings.ModelsRootPath = root;
            settings.DownloaderModelsRootPath = root;
        }));
    }

    private static string ResolveModelsRootPath(AppSettings settings)
    {
        foreach (var candidate in new[] { settings.DownloaderModelsRootPath, settings.ModelsRootPath })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;
        }
        return AppPaths.ModelsDir;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _center.Manager.JobAdded -= OnJobAdded;
        _center.Manager.JobUpdated -= OnJobUpdated;
        _center.Manager.JobRemoved -= OnJobRemoved;
        _center.NoticeRaised -= OnNotice;
    }
}

/// <summary>A goal in the downloader's filter (null = all models).</summary>
public sealed record ModelUseCaseOption(string DisplayName, ModelUseCase? UseCase);

/// <summary>An InfoBar in the download manager for a finished download.</summary>
public sealed class DownloadNoticeViewModel
{
    public DownloadNoticeViewModel(DownloadNotice notice)
    {
        Notice = notice;
    }

    public DownloadNotice Notice { get; }
    public string Title => Notice.Title;
    public string Message => Notice.Message;
    public InfoBarSeverity Severity => Notice.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
}
