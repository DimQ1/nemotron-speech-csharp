using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;
using VoiceType.WinUI.ViewModels;
using WinRT.Interop;

namespace VoiceType.WinUI.Views;

/// <summary>
/// First-run onboarding wizard.
/// Flow: explain & ask consent → download the recommended speech model →
/// set AudioSource=Mix (explained) → load the model → only then allow using the app.
/// Guarantees the model exists before the user can interact with the main window,
/// preventing "model not available" errors.
/// </summary>
public sealed partial class FirstRunWizardWindow : Window, INotifyPropertyChanged
{
    private readonly IModelDownloaderService _downloader;
    private readonly ISettingsService _settingsService;
    private readonly IRecognitionService _recognition;
    private bool _modelLoadedOk;

    public FirstRunWizardWindow()
    {
        _downloader = App.Services.GetRequiredService<IModelDownloaderService>();
        _settingsService = App.Services.GetRequiredService<ISettingsService>();
        _recognition = App.Services.GetRequiredService<IRecognitionService>();

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        ApplyWindowSize();
        ShowStep(WizardStep.Consent);

        _downloader.ProgressChanged += OnDownloadProgress;
        _downloader.StatusChanged += s => DispatcherQueue.TryEnqueue(() => StatusText = s);
        this.Closed += (_, _) => _downloader.Dispose();
    }

    // ---- Bindable state ----

    private double _downloadProgress;
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => SetField(ref _downloadProgress, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private string _progressDetail = "";
    public string ProgressDetail
    {
        get => _progressDetail;
        set => SetField(ref _progressDetail, value);
    }

    private string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        set => SetField(ref _errorText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ---- Wizard step management ----

    private enum WizardStep { Consent, Progress, Done, Error }

    private void ShowStep(WizardStep step)
    {
        StepConsent.Visibility = step == WizardStep.Consent ? Visibility.Visible : Visibility.Collapsed;
        StepProgress.Visibility = step == WizardStep.Progress ? Visibility.Visible : Visibility.Collapsed;
        StepDone.Visibility = step == WizardStep.Done ? Visibility.Visible : Visibility.Collapsed;
        StepError.Visibility = step == WizardStep.Error ? Visibility.Visible : Visibility.Collapsed;

        ConsentButtons.Visibility = step == WizardStep.Consent ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = step == WizardStep.Progress ? Visibility.Visible : Visibility.Collapsed;
        StartUsingButton.Visibility = step == WizardStep.Done ? Visibility.Visible : Visibility.Collapsed;
        ErrorButtons.Visibility = step == WizardStep.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Button handlers ----

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        ShowStep(WizardStep.Progress);
        StatusText = "Starting download…";
        ProgressDetail = "";
        DownloadProgress = 0;

        var repoId = MainViewModel.RecommendedModelRepo; // CPU INT4 opset24 0.56s, ~749 MB
        var subfolder = repoId[(repoId.LastIndexOf('/') + 1)..];
        var settings = _settingsService.Load();
        var modelsRoot = !string.IsNullOrWhiteSpace(settings.ModelsRootPath)
            ? settings.ModelsRootPath
            : AppPaths.ModelsDir;

        bool ok = await RunDownloadAsync(repoId, subfolder, modelsRoot);
        if (!ok) return; // error/cancel already handled

        var modelPath = Path.Combine(modelsRoot, subfolder);

        // Persist: point the app at the freshly downloaded model + default audio mode = Mix.
        settings.ModelsRootPath = modelsRoot;
        settings.SelectedModel = subfolder;
        settings.ModelPath = modelPath;
        settings.AudioSource = "Mix"; // default capture mode, explained on the Done step
        _settingsService.Save(settings);

        // Load the model now so the first launch is error-free.
        StatusText = "Loading model…";
        try
        {
            await _recognition.LoadModelAsync(settings);
            _modelLoadedOk = _recognition.ModelState == ModelState.Loaded;
        }
        catch (Exception ex)
        {
            App.Telemetry?.LogError("FirstRun", $"Model load failed: {ex.Message}");
            _modelLoadedOk = false;
        }

        if (_modelLoadedOk)
        {
            // Notify the already-constructed MainViewModel so it picks up the new
            // model path + Mix audio source and hides the "download a model" banner.
            WeakReferenceMessenger.Default.Send(new SettingsSavedMessage(settings));
            ShowStep(WizardStep.Done);
        }
        else
        {
            ErrorText = "The model was downloaded but failed to load. Check that your PC meets the requirements, then tap Retry.";
            ShowStep(WizardStep.Error);
        }
    }

    private async Task<bool> RunDownloadAsync(string repoId, string subfolder, string modelsRoot)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? errorMessage = null;

        void OnCompleted(bool ok, string msg)
        {
            if (!ok) errorMessage = msg;
            tcs.TrySetResult(ok);
        }

        _downloader.Completed += OnCompleted;
        try
        {
            await _downloader.DownloadModelRepo(repoId, subfolder, modelsRoot);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            tcs.TrySetResult(false);
        }
        finally
        {
            _downloader.Completed -= OnCompleted;
        }

        var okResult = await tcs.Task;
        if (!okResult)
        {
            if (string.Equals(errorMessage, "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                ShowStep(WizardStep.Consent);
            }
            else
            {
                ErrorText = BuildDownloadErrorMessage(errorMessage);
                ShowStep(WizardStep.Error);
            }
        }
        return okResult;
    }

    /// <summary>Turn a raw download exception into actionable guidance. A "user-mapped section"
    /// or file-lock error almost always means ANOTHER VoiceType instance (installed app or a
    /// debug run) currently has the model open — the fix is to close it and retry.</summary>
    private static string BuildDownloadErrorMessage(string? raw)
    {
        var msg = raw ?? "unknown error";
        bool fileLocked = msg.Contains("user-mapped section", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("access to the path", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("is denied", StringComparison.OrdinalIgnoreCase);

        if (fileLocked)
        {
            return "The model file is locked because another copy of VoiceType is running " +
                   "(for example, the installed app AND a debug instance at the same time). " +
                   "Close every other VoiceType window, then tap Retry.";
        }

        return $"Download failed: {msg}. Check your internet connection and try again.";
    }

    private void OnDownloadProgress(DownloadProgress p)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            DownloadProgress = p.OverallProgress;
            StatusText = string.IsNullOrEmpty(p.CurrentFile) ? "Downloading…" : p.CurrentFile;
            ProgressDetail = p.TotalFiles > 0
                ? $"{p.DownloadedFiles}/{p.TotalFiles} files • {p.OverallProgress:F0}%"
                : $"{p.OverallProgress:F0}%";
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _downloader.Cancel();
    }

    private void StartUsing_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();
        ModelPathResolver.ApplyExistingModelPath(settings);
        settings.FirstRunCompleted = true;
        _settingsService.Save(settings);
        this.Close();
    }

    private void Quit_Click(object sender, RoutedEventArgs e)
    {
        // User declined — the app cannot work without a model, so exit entirely.
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    // ---- Window sizing ----

    private void ApplyWindowSize()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetWindowDpi(hwnd);
        var w = (int)(520f * dpi / 96f);
        var h = (int)(560f * dpi / 96f);

        if (hwnd != nint.Zero)
            SetWindowPos(hwnd, 0, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER);

        if (AppWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    // ---- Win32 interop ----

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static int GetWindowDpi(nint hwnd)
    {
        var hmon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        _ = GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out _);
        return (int)dpiX;
    }
}
