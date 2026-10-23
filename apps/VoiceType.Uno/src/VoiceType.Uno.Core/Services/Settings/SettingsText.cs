using SpeechLib.ModelDownload;
using SpeechLib.Translation;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Services;

/// <summary>A choice shown with a human name; <see cref="Value"/> is what <c>settings.json</c> stores.</summary>
public sealed record SettingsOption(string Value, string Title, string? Detail = null, string? Tag = null, bool Enabled = true);

/// <summary>The sections of the Settings page (design voicetype-uno-settings §3.3).</summary>
public enum SettingsSection { Storage, Speech, Audio, Translation, Typing, Hotkeys, General }

/// <summary>
/// Human-readable option names and their mapping to the stored values (design §3.3), plus the section list per platform.
/// Pure text and lists: the page only renders them.
/// </summary>
public static class SettingsText
{
    // ── Sections ─────────────────────────────────────────────────────────────

    /// <summary>Width from which Settings shows the section list and the section side by side.</summary>
    public const double WideBreakpoint = 840;

    /// <summary>Width of the section list on wide windows.</summary>
    public const double ListWidth = 300;

    /// <summary>Widest the section page grows.</summary>
    public const double PaneMaxWidth = 720;

    public static bool IsWide(double width) => width >= WideBreakpoint;

    /// <summary>The sections shown on this platform. Typing into apps and Keyboard shortcuts do not exist on Android.</summary>
    public static IReadOnlyList<SettingsSection> VisibleSections(bool android) =>
        Enum.GetValues<SettingsSection>().Where(s => !android || (s != SettingsSection.Typing && s != SettingsSection.Hotkeys)).ToList();

    /// <summary>What opens first: Storage on wide windows, the hub (null) on narrow ones.</summary>
    public static SettingsSection? InitialSection(bool wide) => wide ? SettingsSection.Storage : null;

    /// <summary>A stored section that may have vanished (platform switch) falls back to the first visible one.</summary>
    public static SettingsSection Coerce(SettingsSection section, bool android) =>
        VisibleSections(android).Contains(section) ? section : VisibleSections(android)[0];

    public static string Title(SettingsSection section) => section switch
    {
        SettingsSection.Storage => "Storage",
        SettingsSection.Speech => "Speech recognition",
        SettingsSection.Audio => "Audio input",
        SettingsSection.Translation => "Translation and Gemma",
        SettingsSection.Typing => "Typing into apps",
        SettingsSection.Hotkeys => "Keyboard shortcuts",
        _ => "General",
    };

    /// <summary>The page title: the section on a narrow window, otherwise "Settings".</summary>
    public static string HeaderTitle(SettingsSection? shown, bool wide) => wide || shown is null ? "Settings" : Title(shown.Value);

    public static string HeaderSubtitle(SettingsSection? shown, bool wide) => wide || shown is null ? "VoiceType" : "Settings";

    // ── Option lists with the value mapping (§3.3) ───────────────────────────

    public const string ComputeCpu = "cpu";
    public const string ComputeGpuPerformance = "webgpu:hp";
    public const string ComputeGpuPowerSaving = "webgpu:lp";
    public const string ComputeFollowModel = "follow_config";

    /// <summary>Compute for speech recognition: <c>cpu</c>, <c>webgpu:hp</c>, <c>webgpu:lp</c>, <c>follow_config</c>.</summary>
    public static IReadOnlyList<SettingsOption> ComputeOptions { get; } =
    [
        new(ComputeCpu, "Processor (CPU)", "Works everywhere", "Recommended"),
        new(ComputeGpuPerformance, "Graphics card — performance",
            "WebGPU on the discrete graphics card, experimental. Nemotron runs its speech encoder there and the rest on the processor; without a discrete card it runs on the processor"),
        new(ComputeGpuPowerSaving, "Graphics card — power saving",
            "WebGPU on the integrated graphics. Nemotron does not use it: integrated graphics are slower than the processor, so it runs on the processor"),
        new(ComputeFollowModel, "As set by the model", "The model files do not choose a provider, so this runs on the processor"),
    ];

    /// <summary>
    /// The option a stored value selects. Anything else (a stale <c>cuda</c> / <c>dml</c> from the GPU builds) is run on the
    /// processor by the recognizer, so the page shows Processor selected.
    /// </summary>
    public static string NormalizeCompute(string? stored) =>
        ComputeOptions.FirstOrDefault(o => string.Equals(o.Value, stored?.Trim(), StringComparison.OrdinalIgnoreCase))?.Value ?? ComputeCpu;

    public static string ComputeTitle(string? stored) => ComputeOptions.First(o => o.Value == NormalizeCompute(stored)).Title;

    /// <summary>Short form for the section subtitle: <c>CPU</c>, <c>GPU</c>, <c>GPU (power saving)</c>, <c>CPU (by model)</c> (the model files choose no provider: it runs on the processor).</summary>
    public static string ComputeShort(string? stored) => NormalizeCompute(stored) switch
    {
        ComputeGpuPerformance => "GPU",
        ComputeGpuPowerSaving => "GPU (power saving)",
        ComputeFollowModel => "CPU (by model)",
        _ => "CPU",
    };

    public const string SourceMic = "Mic";
    public const string SourceLoopback = "Loopback";
    public const string SourceMix = "Mix";

    /// <summary>Audio input: <c>Mic</c>, <c>Loopback</c>, <c>Mix</c>. On Android only the microphone works.</summary>
    public static IReadOnlyList<SettingsOption> AudioSourceOptions(bool android) => android
        ?
        [
            new(SourceMic, "Microphone", "The phone microphone or a connected headset"),
            new(SourceLoopback, "System audio", "Not available on Android: apps cannot record other apps’ sound", Enabled: false),
        ]
        :
        [
            new(SourceMic, "Microphone", "The default input device in your system sound settings"),
            new(SourceLoopback, "System audio", "What you hear: calls, videos, meetings (loopback)"),
            new(SourceMix, "Microphone + system audio", "Both sides of a call in one transcript"),
        ];

    /// <summary>The audio source a stored value selects (case-insensitive; unknown or unavailable falls back to the microphone).</summary>
    public static string NormalizeAudioSource(string? stored, bool android)
    {
        var match = AudioSourceOptions(android).FirstOrDefault(o => o.Enabled && string.Equals(o.Value, stored?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Value ?? SourceMic;
    }

    public static string AudioSourceTitle(string? stored, bool android) =>
        AudioSourceOptions(android).First(o => o.Value == NormalizeAudioSource(stored, android)).Title;

    public const string EngineNative = "native";
    public const string EngineHttp = "http";

    /// <summary>Translation engine: <c>native</c> = On this device, <c>http</c> = Local server.</summary>
    public static IReadOnlyList<SettingsOption> EngineOptions { get; } =
    [
        new(EngineNative, "On this device", "Gemma runs inside VoiceType · offline"),
        new(EngineHttp, "Local server", "A LiteRT-LM server with an OpenAI-compatible API"),
    ];

    public static string NormalizeEngine(string? stored) =>
        string.Equals(stored?.Trim(), EngineHttp, StringComparison.OrdinalIgnoreCase) ? EngineHttp : EngineNative;

    public static string EngineTitle(string? stored) => EngineOptions.First(o => o.Value == NormalizeEngine(stored)).Title;

    /// <summary>Gemma compute: <c>cpu</c> = Processor, <c>gpu</c> = Graphics card.</summary>
    public static IReadOnlyList<SettingsOption> GemmaComputeOptions { get; } =
    [
        new("cpu", "Processor"),
        new("gpu", "Graphics card"),
    ];

    public static string NormalizeGemmaCompute(string? stored) =>
        string.Equals(stored?.Trim(), "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";

    public const string InjectionType = "type";
    public const string InjectionPaste = "paste";

    /// <summary>How a phrase gets into the focused window: <c>type</c> = key presses, <c>paste</c> = clipboard + shortcut.</summary>
    public static IReadOnlyList<SettingsOption> InjectionMethodOptions { get; } =
    [
        new(InjectionType, "Type the text", "Sent as key presses · the clipboard is not touched"),
        new(InjectionPaste, "Paste from the clipboard", "For apps that ignore typed keys · the phrase stays on the clipboard"),
    ];

    /// <summary>The method a stored value selects; anything but <c>paste</c> is <c>type</c>.</summary>
    public static string NormalizeInjectionMethod(string? stored) =>
        string.Equals(stored?.Trim(), InjectionPaste, StringComparison.OrdinalIgnoreCase) ? InjectionPaste : InjectionType;

    public static TextInjectionMode InjectionModeOf(string? stored) =>
        NormalizeInjectionMethod(stored) == InjectionPaste ? TextInjectionMode.Paste : TextInjectionMode.Type;

    public static IReadOnlyList<SettingsOption> PasteChordOptions { get; } =
    [
        new("Ctrl+V", "Ctrl+V", "Most apps"),
        new("Ctrl+Shift+V", "Ctrl+Shift+V", "Terminals: Windows Terminal, VS Code terminal"),
        new("Shift+Insert", "Shift+Insert", "Older terminals and remote desktops"),
    ];

    /// <summary>The paste shortcut a stored value selects; a hand-edited chord is kept as its own extra option by the page.</summary>
    public static string NormalizePasteChord(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? "Ctrl+V" : stored.Trim();

    /// <summary>The list for the paste-shortcut radios, with a custom stored chord added so it is not silently lost.</summary>
    public static IReadOnlyList<SettingsOption> PasteChordOptionsFor(string? stored)
    {
        var value = NormalizePasteChord(stored);
        return PasteChordOptions.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))
            ? PasteChordOptions
            : [.. PasteChordOptions, new SettingsOption(value, value, "Set in settings.json")];
    }

    public static IReadOnlyList<double> RepetitionPenaltyChoices { get; } = [1.0, 1.1, 1.2, 1.5];

    public static string RepetitionLabel(double value) => value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The nearest offered penalty (settings files may hold any number).</summary>
    public static double NearestRepetition(double stored) =>
        RepetitionPenaltyChoices.OrderBy(c => Math.Abs(c - stored)).First();

    // ── Languages ────────────────────────────────────────────────────────────

    /// <summary>Recognition language codes: <c>auto</c> plus the languages the model families know.</summary>
    public static IReadOnlyList<string> LanguageCodes { get; } =
    [
        "auto", "en", "ru", "de", "fr", "es", "zh", "ja", "ko", "pt",
        "it", "ar", "hi", "tr", "uk", "pl", "nl",
    ];

    /// <summary>Recognition languages with names: <c>auto</c> is "Auto-detect", the rest use the shared language names.</summary>
    public static IReadOnlyList<SettingsOption> RecognitionLanguages { get; } =
        LanguageCodes.Select(code => code == "auto"
            ? new SettingsOption("auto", "Auto-detect")
            : new SettingsOption(code, TranslationLanguages.NameFor(code) ?? code)).ToList();

    /// <summary>The recognition language a stored code selects (unknown codes keep their own entry).</summary>
    public static string LanguageTitle(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            return "Auto-detect";
        return RecognitionLanguages.FirstOrDefault(o => string.Equals(o.Value, code.Trim(), StringComparison.OrdinalIgnoreCase))?.Title ?? code.Trim();
    }

    /// <summary>Translation targets: the shared catalog (names, not codes).</summary>
    public static IReadOnlyList<SettingsOption> TranslationTargets { get; } =
        TranslationLanguages.All.Select(l => new SettingsOption(l.Code, l.Name)).ToList();

    /// <summary>The display name of a stored target ("ru" → Russian; a typed name such as Belarusian stays as typed).</summary>
    public static string TargetTitle(string? stored) => TranslationLanguages.NameFor(stored) ?? "Russian";

    /// <summary>True when the stored target is none of the catalog languages (shown through the "Other…" field).</summary>
    public static bool IsCustomTarget(string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && TranslationTargets.All(t =>
            !string.Equals(t.Value, stored.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(t.Title, stored.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── Section subtitles (the hub's second line) ────────────────────────────

    /// <summary>Facts the subtitles need that are not in <see cref="AppSettings"/> (measured storage).</summary>
    public sealed record SubtitleContext(bool Android, string? StorageLine = null);

    public static string Subtitle(SettingsSection section, AppSettings settings, SubtitleContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);
        return section switch
        {
            SettingsSection.Storage => context.StorageLine ?? "Models, recordings and app data",
            SettingsSection.Speech => HistoryUiFormat.Join(
                string.IsNullOrWhiteSpace(settings.SelectedModel) ? "No model selected" : ModelCatalog.DescribeFolder(settings.SelectedModel),
                ComputeShort(settings.ExecutionProvider),
                IsAuto(settings.Language) ? "auto language" : LanguageTitle(settings.Language)),
            SettingsSection.Audio => AudioSourceTitle(settings.AudioSource, context.Android),
            SettingsSection.Translation => settings.TranslationEnabled
                ? $"Into {TargetTitle(settings.TranslationTargetLanguage)} · Gemma {(NormalizeEngine(settings.TranslationBackend) == EngineHttp ? "on a local server" : "on this device")}"
                : "Off",
            SettingsSection.Typing => !settings.IsTextInjectionEnabled
                ? "Off"
                : InjectionModeOf(settings.TextInjectionMethod) == TextInjectionMode.Paste
                    ? $"On · paste with {NormalizePasteChord(settings.PasteChord)}"
                    : "On · typed as key presses",
            SettingsSection.Hotkeys => HotkeyText.Summary(settings),
            _ => GeneralSummary(settings, context.Android),
        };
    }

    /// <summary><c>Window on top · auto-scroll</c>; what is on, in the order of the page.</summary>
    public static string GeneralSummary(AppSettings settings, bool android)
    {
        var parts = new List<string>();
        if (!android && settings.AlwaysOnTop)
            parts.Add("Window on top");
        if (settings.IsAutoScrollEnabled)
            parts.Add("auto-scroll");
        if (settings.AutoStartRecognition)
            parts.Add("auto-start");
        return parts.Count == 0 ? "Everything off" : string.Join(" · ", parts);
    }

    /// <summary>Subtitle of the History link: <c>Keep audio 30 days · limit 2 GB</c>.</summary>
    public static string HistoryLinkSubtitle(HistorySettings history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var keep = history.AudioRetentionDays <= 0 ? "Keep audio forever" : "Keep audio " + HistoryUiStorage.RetentionLabel(history.AudioRetentionDays);
        var limit = history.MaxAudioStorageMb > 0 ? "limit " + HistoryUiStorage.StorageCapLabel(history.MaxAudioStorageMb) : "no limit";
        return keep + " · " + limit;
    }

    /// <summary>Retention card in Storage › Recordings: <c>Keep audio 30 days · limit 2 GB · Standard quality</c>.</summary>
    public static string RetentionLinkSubtitle(HistorySettings history) =>
        HistoryLinkSubtitle(history) + " · " + QualityName(history.AudioQuality) + " quality";

    private static string QualityName(AudioQuality quality) => quality switch
    {
        AudioQuality.High => "High",
        AudioQuality.Compact => "Compact",
        _ => "Standard",
    };

    public const string ModelsLinkSubtitle = "Download, switch, delete";

    // ── Captions ─────────────────────────────────────────────────────────────

    /// <summary>Under the Compute group.</summary>
    public const string ReloadCaption = "Changing the model or compute reloads the model. During a recording the change waits until you stop.";

    /// <summary>Shown while a change is waiting for Stop.</summary>
    public const string WaitingForStopCaption = "Waiting for you to stop recording: the model reloads then.";

    public const string AudioCaptionDesktop =
        "System audio is captured from the default output and the communications device, so headset calls are included. Changing the source restarts capture without stopping the recording.";

    public const string AudioCaptionAndroid =
        "Recording continues with the screen off. Android shows a notification while VoiceType listens.";

    public const string ServerUrlHint = "/v1/chat/completions is added automatically";
    public const string ServerUrlError = "Enter an address like http://localhost:9379";

    /// <summary>An absolute http or https address (what the local server setting needs).</summary>
    public static bool IsValidServerUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The note under the Typing options for the chosen method.</summary>
    public static string TypingCaptionFor(string? method) =>
        NormalizeInjectionMethod(method) == InjectionPaste
            ? "VoiceType puts the phrase on the clipboard and sends the shortcut, so the phrase stays on the clipboard."
            : "VoiceType types the phrase key by key, so your clipboard stays as it is. If an app ignores it, choose Paste.";

    public const string ResetSubtitle = "Folders, models and history are not touched";

    private static bool IsAuto(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
}
