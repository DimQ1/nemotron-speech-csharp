using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VoiceType.Models;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>
/// ViewModel for the separate Voice Commands (Voice Intent Mode) settings window.
/// All settings for the optional voice-command mode and its intent model live here.
/// </summary>
public sealed class VoiceCommandsSettingsViewModel : INotifyPropertyChanged
{
    private readonly VoiceIntentSettings _settings;
    private readonly WindowTargetHistory? _history;

    private bool _enabled;
    private string _intentModelPath = "";
    private float _confidenceThreshold;
    private int _hysteresisM;
    private int _hysteresisN;
    private bool _rememberTargets;
    private bool _confirmTargetSwitch;
    private int _historyPerProcess;
    private string _customIntentsFile = "";
    private string _appAliasesText = "";
    private string _modelStatus = "";

    public VoiceCommandsSettingsViewModel(VoiceIntentSettings settings, WindowTargetHistory? history)
    {
        _settings = settings;
        _history = history;

        _enabled = settings.Enabled;
        _intentModelPath = settings.IntentModelPath;
        _confidenceThreshold = settings.ConfidenceThreshold;
        _hysteresisM = settings.HysteresisM;
        _hysteresisN = settings.HysteresisN;
        _rememberTargets = settings.RememberTargets;
        _confirmTargetSwitch = settings.ConfirmTargetSwitch;
        _historyPerProcess = settings.HistoryPerProcess;
        _customIntentsFile = settings.CustomIntentsFile;
        _appAliasesText = SerializeAliases(settings.AppAliases);

        RefreshTargets();
        UpdateModelStatus();

        ClearHistoryCommand = new RelayCommand(ClearHistory, () => Targets.Count > 0);
        RefreshHistoryCommand = new RelayCommand(RefreshTargets);
    }

    public ObservableCollection<WindowTarget> Targets { get; } = new();

    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }

    public bool Enabled
    {
        get => _enabled;
        set { if (SetProperty(ref _enabled, value)) UpdateModelStatus(); }
    }

    public string IntentModelPath
    {
        get => _intentModelPath;
        set { if (SetProperty(ref _intentModelPath, value)) UpdateModelStatus(); }
    }

    public float ConfidenceThreshold { get => _confidenceThreshold; set => SetProperty(ref _confidenceThreshold, value); }
    public int HysteresisM { get => _hysteresisM; set => SetProperty(ref _hysteresisM, value); }
    public int HysteresisN { get => _hysteresisN; set => SetProperty(ref _hysteresisN, value); }
    public bool RememberTargets { get => _rememberTargets; set => SetProperty(ref _rememberTargets, value); }
    public bool ConfirmTargetSwitch { get => _confirmTargetSwitch; set => SetProperty(ref _confirmTargetSwitch, value); }
    public int HistoryPerProcess { get => _historyPerProcess; set => SetProperty(ref _historyPerProcess, value); }
    public string CustomIntentsFile { get => _customIntentsFile; set => SetProperty(ref _customIntentsFile, value); }
    public string AppAliasesText { get => _appAliasesText; set => SetProperty(ref _appAliasesText, value); }

    public string ModelStatus { get => _modelStatus; private set => SetProperty(ref _modelStatus, value); }

    /// <summary>Model file presence, for the "model loaded" indicator.</summary>
    public bool ModelFilesPresent =>
        !string.IsNullOrWhiteSpace(IntentModelPath)
        && System.IO.File.Exists(System.IO.Path.Combine(IntentModelPath, "onnx", "model_int8.onnx"))
        && System.IO.File.Exists(System.IO.Path.Combine(IntentModelPath, "sentencepiece.bpe.model"));

    private void UpdateModelStatus()
    {
        if (!Enabled)
            ModelStatus = "Mode off — no model loaded.";
        else if (string.IsNullOrWhiteSpace(IntentModelPath))
            ModelStatus = "L0 heuristics only (no semantic model).";
        else if (ModelFilesPresent)
            ModelStatus = "Semantic model files found (MiniLM INT8, CPU).";
        else
            ModelStatus = "Model path set but files not found — L0 fallback until downloaded.";
        OnPropertyChanged(nameof(ModelFilesPresent));
    }

    private void RefreshTargets()
    {
        Targets.Clear();
        if (_history is null) return;
        foreach (var t in _history.Targets.OrderByDescending(t => t.LastUsed))
            Targets.Add(t);
    }

    private void ClearHistory()
    {
        _history?.Clear();
        RefreshTargets();
    }

    /// <summary>Persist VM state back into the shared settings object.</summary>
    public void ApplyTo(VoiceIntentSettings target)
    {
        target.Enabled = Enabled;
        target.IntentModelPath = IntentModelPath.Trim();
        target.ConfidenceThreshold = ConfidenceThreshold;
        target.HysteresisM = HysteresisM;
        target.HysteresisN = HysteresisN;
        target.RememberTargets = RememberTargets;
        target.ConfirmTargetSwitch = ConfirmTargetSwitch;
        target.HistoryPerProcess = HistoryPerProcess;
        target.CustomIntentsFile = CustomIntentsFile.Trim();
        target.AppAliases = ParseAliases(AppAliasesText);
    }

    private static string SerializeAliases(IReadOnlyDictionary<string, string[]> aliases)
        => string.Join(Environment.NewLine,
            aliases.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {string.Join(", ", kv.Value)}"));

    private static Dictionary<string, string[]> ParseAliases(string text)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var proc = raw[..eq].Trim().TrimEnd('.');
            if (proc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                proc = proc[..^4];
            var names = raw[(eq + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (proc.Length > 0 && names.Length > 0)
                result[proc] = names;
        }
        return result;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
