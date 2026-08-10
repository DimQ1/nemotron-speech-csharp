using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

/// <summary>Dedicated editor for the optional Voice Intent feature and app destinations.</summary>
public sealed partial class VoiceCommandsSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly VoiceCommandAppCatalog _appCatalog;

    public nint OwnerWindowHandle { get; set; }
    public event Action? RequestClose;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _intentModelPath = "";

    [ObservableProperty]
    private float _confidenceThreshold = 0.85f;

    [ObservableProperty]
    private int _hysteresisM = 2;

    [ObservableProperty]
    private int _hysteresisN = 3;

    [ObservableProperty]
    private bool _rememberTargets = true;

    [ObservableProperty]
    private bool _confirmTargetSwitch = true;

    [ObservableProperty]
    private int _historyPerProcess = 10;

    [ObservableProperty]
    private string _aliasesText = "";

    [ObservableProperty]
    private string _customIntentsFile = "";

    [ObservableProperty]
    private string _newApplicationName = "";

    [ObservableProperty]
    private string _newApplicationPath = "";

    [ObservableProperty]
    private string _newApplicationAliases = "";

    [ObservableProperty]
    private string _status = "";

    public ObservableCollection<VoiceCommandApp> Applications { get; } = new();
    public ObservableCollection<VoiceCommandApp> DiscoveredApplications { get; } = new();

    public VoiceCommandsSettingsViewModel(
        ISettingsService settingsService,
        VoiceCommandAppCatalog appCatalog,
        AppSettings settings)
    {
        _settingsService = settingsService;
        _appCatalog = appCatalog;

        var voice = settings.VoiceIntents ?? new VoiceIntentSettings();
        Enabled = voice.Enabled;
        IntentModelPath = voice.IntentModelPath;
        ConfidenceThreshold = voice.ConfidenceThreshold;
        HysteresisM = voice.HysteresisM;
        HysteresisN = voice.HysteresisN;
        RememberTargets = voice.RememberTargets;
        ConfirmTargetSwitch = voice.ConfirmTargetSwitch;
        HistoryPerProcess = voice.HistoryPerProcess;
        AliasesText = SerializeAliases(voice.AppAliases);
        CustomIntentsFile = voice.CustomIntentsFile;

        foreach (var app in voice.Applications)
            Applications.Add(app.Clone());

        RefreshCatalogue();
    }

    [RelayCommand]
    private async Task BrowseApplication()
    {
        var path = await FileBrowser.PickExecutableAsync(OwnerWindowHandle);
        if (path is null)
            return;

        NewApplicationPath = path;
        if (string.IsNullOrWhiteSpace(NewApplicationName))
            NewApplicationName = Path.GetFileNameWithoutExtension(path);
    }

    [RelayCommand]
    private void AddApplication()
    {
        var path = NewApplicationPath.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            Status = "Choose an existing .exe file.";
            return;
        }

        var processName = Path.GetFileNameWithoutExtension(path);
        var displayName = string.IsNullOrWhiteSpace(NewApplicationName)
            ? processName
            : NewApplicationName.Trim();

        var existing = Applications.FirstOrDefault(app =>
            string.Equals(app.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Status = "This application is already configured.";
            return;
        }

        Applications.Add(new VoiceCommandApp
        {
            DisplayName = displayName,
            ProcessName = processName,
            ExecutablePath = path,
            Aliases = NewApplicationAliases
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
        });
        NewApplicationName = "";
        NewApplicationPath = "";
        NewApplicationAliases = "";
        Status = "Application added.";
        RefreshCatalogue();
    }

    [RelayCommand]
    private void RemoveApplication(VoiceCommandApp? app)
    {
        if (app is null)
            return;

        Applications.Remove(app);
        RefreshCatalogue();
    }

    [RelayCommand]
    private void RefreshCatalogue()
    {
        DiscoveredApplications.Clear();
        foreach (var app in _appCatalog.GetApplications(Applications))
            DiscoveredApplications.Add(app);
        Status = $"Found {DiscoveredApplications.Count} available applications.";
    }

    [RelayCommand]
    private async Task Save()
    {
        var voice = BuildVoiceSettings();
        await Task.Run(() => _settingsService.Update(settings => settings.VoiceIntents = voice));
        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage(_settingsService.Load()));
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    private VoiceIntentSettings BuildVoiceSettings() => new()
    {
        Enabled = Enabled,
        IntentModelPath = IntentModelPath.Trim(),
        ConfidenceThreshold = Math.Clamp(ConfidenceThreshold, 0.5f, 1f),
        HysteresisM = Math.Max(1, HysteresisM),
        HysteresisN = Math.Max(Math.Max(1, HysteresisM), HysteresisN),
        RememberTargets = RememberTargets,
        ConfirmTargetSwitch = ConfirmTargetSwitch,
        HistoryPerProcess = Math.Max(1, HistoryPerProcess),
        AppAliases = ParseAliases(AliasesText),
        CustomIntentsFile = CustomIntentsFile.Trim(),
        Applications = Applications.Select(app => app.Clone()).ToList(),
    };

    private static string SerializeAliases(IReadOnlyDictionary<string, string[]> aliases) =>
        string.Join(Environment.NewLine, aliases.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key} = {string.Join(", ", pair.Value)}"));

    private static Dictionary<string, string[]> ParseAliases(string? text)
    {
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var process = line[..separator].Trim().TrimEnd('.');
            if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                process = process[..^4];

            var names = line[(separator + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (process.Length > 0 && names.Length > 0)
                aliases[process] = names;
        }

        return aliases;
    }
}