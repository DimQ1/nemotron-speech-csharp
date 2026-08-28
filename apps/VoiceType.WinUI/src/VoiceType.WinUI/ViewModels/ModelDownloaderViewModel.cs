using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

public sealed partial class ModelDownloaderViewModel : ObservableObject, IDisposable
{
    private readonly IModelDownloaderService _service;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher;
    private string? _resultModelPath;

    public nint OwnerWindowHandle { get; set; }

    // ---- Observable properties ----

    [ObservableProperty]
    private string _modelsRootPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isDownloading;

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private string _currentFile = "";

    [ObservableProperty]
    private string _fileRemaining = "";

    [ObservableProperty]
    private double _fileProgress;

    [ObservableProperty]
    private string _folderRemaining = "";

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private int _downloadedFiles;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private ModelCardViewModel? _selectedModel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredModels))]
    private ModelUseCaseOption? _selectedUseCase;

    public bool IsIdle => !IsDownloading;
    public string FileProgressDisplay => FileProgress > 0 ? $"{FileProgress:F0}%" : "";
    public string DownloadProgressDisplay => DownloadProgress > 0 ? $"{DownloadProgress:F0}%" : "";

    public string? ResultPath { get; private set; }
    public string? ResultModelPath { get; private set; }
    public bool WasDownloaded => ResultPath is not null;

    // ---- Predefined models ----

    public IReadOnlyList<ModelCardViewModel> Models { get; } =
        ModelCatalog.Models.Select(m => new ModelCardViewModel(m)).ToList();

    public IReadOnlyList<ModelUseCaseOption> UseCaseOptions { get; } =
    [
        new ModelUseCaseOption("Fast dictation — type as you speak", ModelUseCase.FastDictation),
        new ModelUseCaseOption("Higher quality — slight lag", ModelUseCase.HighQuality),
        new ModelUseCaseOption("Multilingual — 25 languages", ModelUseCase.Multilingual),
    ];

    public IReadOnlyList<ModelCardViewModel> FilteredModels =>
        Models.Where(m => m.Descriptor.UseCase == SelectedUseCase?.UseCase).ToList();

    // ---- Constructor ----

    public ModelDownloaderViewModel(
        IModelDownloaderService service,
        ISettingsService settingsService,
        DispatcherQueue dispatcher)
    {
        _service = service;
        _settingsService = settingsService;
        _dispatcher = dispatcher;

        var settings = settingsService.Load();
        ModelsRootPath = ResolveModelsRootPath(settings);

        SelectedUseCase = UseCaseOptions[0];

        _service.StatusChanged += s => _dispatcher.TryEnqueue(() => Status = s);
        _service.ProgressChanged += OnProgress;
        _service.Completed += OnCompleted;
    }

    // ---- Property change hooks ----

    partial void OnSelectedModelChanged(ModelCardViewModel? value)
    {
        DownloadProgress = 0;
        FileProgress = 0;
        Status = value is not null
            ? $"Selected: {value.CommercialName} · {value.Variant}"
            : "Ready";
    }

    partial void OnSelectedUseCaseChanged(ModelUseCaseOption? value)
    {
        if (value is null) return;
        SelectedModel = Models.FirstOrDefault(m => m.Descriptor.UseCase == value.UseCase && m.Descriptor.IsRecommended)
            ?? Models.FirstOrDefault(m => m.Descriptor.UseCase == value.UseCase);
    }

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        var model = SelectedModel?.Descriptor;
        if (model is null) return;

        ResultPath = ResultModelPath = null;
        IsDownloading = true;
        FolderRemaining = "";
        FileRemaining = "";
        CurrentFile = "";
        FileProgress = 0;
        DownloadProgress = 0;
        DownloadedFiles = 0;
        TotalFiles = 0;

        var subfolder = model.SubfolderName;
        _resultModelPath = Path.Combine(ModelsRootPath, subfolder);

        try
        {
            await _service.DownloadModelRepo(model.RepoId, subfolder, ModelsRootPath, QuantizationFolder: model.QuantizationFolder);
        }
        catch (OperationCanceledException)
        {
            Status = "Download cancelled";
            IsDownloading = false;
        }
        catch (Exception ex)
        {
            Status = $"Download error: {ex.Message}";
            IsDownloading = false;
        }
    }

    private bool CanDownload() => SelectedModel is not null && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _service.Cancel();

    private bool CanCancel() => IsDownloading;

    [RelayCommand]
    private async Task BrowseRoot()
    {
        var initialPath = Directory.Exists(ModelsRootPath) ? ModelsRootPath
            : Services.AppPaths.DataRoot;
        var path = await FolderBrowser.ShowAsync("Select root folder for downloaded models", initialPath, OwnerWindowHandle);
        if (path is not null) ModelsRootPath = path;
    }

    // ---- Progress handlers ----

    private void OnProgress(DownloadProgress p)
    {
        _dispatcher.TryEnqueue(() =>
        {
            CurrentFile = p.CurrentFile;
            FileProgress = p.FileProgress;
            DownloadProgress = p.OverallProgress;
            DownloadedFiles = p.DownloadedFiles;
            TotalFiles = p.TotalFiles;

            FileRemaining = p.FileProgress > 0 ? $"({100 - p.FileProgress:F0}% left)" : "";
            FolderRemaining = p.OverallProgress > 0 ? $"({100 - p.OverallProgress:F0}% remaining)" : "";
        });
    }

    private void OnCompleted(bool ok, string msg)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsDownloading = false;
            if (ok)
            {
                ResultPath = ModelsRootPath;
                ResultModelPath = _resultModelPath;
                Status = "Download complete!";
            }
            else
            {
                ResultPath = ResultModelPath = null;
                Status = msg;
            }
        });
    }

    // ---- Helpers ----

    private static string ResolveModelsRootPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath) && Directory.Exists(settings.ModelsRootPath))
            return settings.ModelsRootPath;
        return Services.AppPaths.ModelsDir;
    }

    public void Dispose() => _service.Dispose();
}

/// <summary>A user-facing goal shown in the downloader's use-case selector.</summary>
public sealed record ModelUseCaseOption(string DisplayName, ModelUseCase UseCase);