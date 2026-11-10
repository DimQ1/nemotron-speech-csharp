using SpeechLib.ModelDownload;
using SpeechLib.Translation;
using VoiceType.Uno.History;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Services;

/// <summary>A choice shown with a human name; <see cref="Value"/> is what <c>settings.json</c> stores.</summary>
public sealed record SettingsOption(string Value, string Title, string? Detail = null, string? Tag = null, bool Enabled = true);

/// <summary>Icon family of an audio source: the microphone, what the computer plays, or both.</summary>
public enum AudioSourceGlyph
{
    Microphone,
    SystemAudio,
    Both,
}

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
        SettingsSection.Storage => Loc.T("Settings.Section.Storage"),
        SettingsSection.Speech => Loc.T("Settings.Section.Speech"),
        SettingsSection.Audio => Loc.T("Settings.Section.Audio"),
        SettingsSection.Translation => Loc.T("Settings.Section.Translation"),
        SettingsSection.Typing => Loc.T("Settings.Section.Typing"),
        SettingsSection.Hotkeys => Loc.T("Settings.Section.Hotkeys"),
        _ => Loc.T("Settings.Section.General"),
    };

    /// <summary>The page title: the section on a narrow window, otherwise "Settings".</summary>
    public static string HeaderTitle(SettingsSection? shown, bool wide) => wide || shown is null ? Loc.T("Settings.Page.Title") : Title(shown.Value);

    public static string HeaderSubtitle(SettingsSection? shown, bool wide) => wide || shown is null ? Loc.T("Settings.Page.Subtitle") : Loc.T("Settings.Page.Title");

    // ── Option lists with the value mapping (§3.3) ───────────────────────────

    public const string ComputeCpu = "cpu";
    public const string ComputeGpuPerformance = "webgpu:hp";
    public const string ComputeGpuPowerSaving = "webgpu:lp";
    public const string ComputeFollowModel = "follow_config";

    /// <summary>Compute for speech recognition: <c>cpu</c>, <c>webgpu:hp</c>, <c>webgpu:lp</c>, <c>follow_config</c>.</summary>
    public static IReadOnlyList<SettingsOption> ComputeOptions =>
    [
        new(ComputeCpu, Loc.T("Settings.Compute.Cpu.Title"), Loc.T("Settings.Compute.Cpu.Detail"), Loc.T("Settings.Compute.Cpu.Tag")),
        new(ComputeGpuPerformance, Loc.T("Settings.Compute.GpuPerformance.Title"), Loc.T("Settings.Compute.GpuPerformance.Detail")),
        new(ComputeGpuPowerSaving, Loc.T("Settings.Compute.GpuPowerSaving.Title"), Loc.T("Settings.Compute.GpuPowerSaving.Detail")),
        new(ComputeFollowModel, Loc.T("Settings.Compute.FollowModel.Title"), Loc.T("Settings.Compute.FollowModel.Detail")),
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
        ComputeGpuPerformance => Loc.T("Settings.Compute.Short.Gpu"),
        ComputeGpuPowerSaving => Loc.T("Settings.Compute.Short.GpuPowerSaving"),
        ComputeFollowModel => Loc.T("Settings.Compute.Short.FollowModel"),
        _ => Loc.T("Settings.Compute.Short.Cpu"),
    };

    public const string SourceMic = "Mic";
    public const string SourceLoopback = "Loopback";
    public const string SourceMix = "Mix";

    /// <summary>Audio input: <c>Mic</c>, <c>Loopback</c>, <c>Mix</c>. On Android only the microphone works.</summary>
    public static IReadOnlyList<SettingsOption> AudioSourceOptions(bool android) => android
        ?
        [
            new(SourceMic, Loc.T("Settings.Audio.Mic.Title"), Loc.T("Settings.Audio.Mic.DetailAndroid")),
            new(SourceLoopback, Loc.T("Settings.Audio.Loopback.Title"), Loc.T("Settings.Audio.Loopback.DetailAndroid"), Enabled: false),
        ]
        :
        [
            new(SourceMic, Loc.T("Settings.Audio.Mic.Title"), Loc.T("Settings.Audio.Mic.Detail")),
            new(SourceLoopback, Loc.T("Settings.Audio.Loopback.Title"), Loc.T("Settings.Audio.Loopback.Detail")),
            new(SourceMix, Loc.T("Settings.Audio.Mix.Title"), Loc.T("Settings.Audio.Mix.Detail")),
        ];

    /// <summary>The audio source a stored value selects (case-insensitive; unknown or unavailable falls back to the microphone).</summary>
    public static string NormalizeAudioSource(string? stored, bool android)
    {
        var match = AudioSourceOptions(android).FirstOrDefault(o => o.Enabled && string.Equals(o.Value, stored?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match?.Value ?? SourceMic;
    }

    public static string AudioSourceTitle(string? stored, bool android) =>
        AudioSourceOptions(android).First(o => o.Value == NormalizeAudioSource(stored, android)).Title;

    /// <summary>Whether the user has a real choice: more than one source can be used (false on Android, where only the microphone works).</summary>
    public static bool HasAudioSourceChoice(bool android) => AudioSourceOptions(android).Count(o => o.Enabled) > 1;

    /// <summary>The source on the main screen chip: short enough for a phone-width screen next to its icon.</summary>
    public static string AudioSourceChipTitle(string? stored, bool android) => NormalizeAudioSource(stored, android) switch
    {
        SourceLoopback => Loc.T("Settings.Audio.Chip.Loopback"),
        SourceMix => Loc.T("Settings.Audio.Chip.Mix"),
        _ => Loc.T("Settings.Audio.Chip.Mic"),
    };

    /// <summary>The icon family of a source; the head maps it to its symbol.</summary>
    public static AudioSourceGlyph AudioSourceGlyphOf(string? stored, bool android) => NormalizeAudioSource(stored, android) switch
    {
        SourceLoopback => AudioSourceGlyph.SystemAudio,
        SourceMix => AudioSourceGlyph.Both,
        _ => AudioSourceGlyph.Microphone,
    };

    /// <summary>What a screen reader says for the chip: the source, and that it can be changed when it can.</summary>
    public static string AudioSourceChipName(string? stored, bool android) => HasAudioSourceChoice(android)
        ? Loc.F("Settings.Audio.Chip.NameChangeable", AudioSourceTitle(stored, android))
        : Loc.F("Settings.Audio.Chip.Name", AudioSourceTitle(stored, android));

    /// <summary>Hint at the end of the source menu while recording: the switch happens on the fly, the recording is not interrupted.</summary>
    public static string AudioSourceSwitchWhileRecordingHint => Loc.T("Settings.Audio.SwitchWhileRecordingHint");

    public const string EngineNative = "native";
    public const string EngineHttp = "http";

    /// <summary>Translation engine: <c>native</c> = On this device, <c>http</c> = Local server.</summary>
    public static IReadOnlyList<SettingsOption> EngineOptions =>
    [
        new(EngineNative, Loc.T("Settings.Translation.Engine.Native.Title"), Loc.T("Settings.Translation.Engine.Native.Detail")),
        new(EngineHttp, Loc.T("Settings.Translation.Engine.Http.Title"), Loc.T("Settings.Translation.Engine.Http.Detail")),
    ];

    public static string NormalizeEngine(string? stored) =>
        string.Equals(stored?.Trim(), EngineHttp, StringComparison.OrdinalIgnoreCase) ? EngineHttp : EngineNative;

    public static string EngineTitle(string? stored) => EngineOptions.First(o => o.Value == NormalizeEngine(stored)).Title;

    /// <summary>Gemma compute: <c>cpu</c> = Processor, <c>gpu</c> = Graphics card.</summary>
    public static IReadOnlyList<SettingsOption> GemmaComputeOptions =>
    [
        new("cpu", Loc.T("Settings.Translation.GemmaCompute.Cpu")),
        new("gpu", Loc.T("Settings.Translation.GemmaCompute.Gpu")),
    ];

    public static string NormalizeGemmaCompute(string? stored) =>
        string.Equals(stored?.Trim(), "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";

    public const string InjectionType = "type";
    public const string InjectionPaste = "paste";

    /// <summary>How a phrase gets into the focused window: <c>type</c> = key presses, <c>paste</c> = clipboard + shortcut.</summary>
    public static IReadOnlyList<SettingsOption> InjectionMethodOptions =>
    [
        new(InjectionType, Loc.T("Settings.Typing.Method.Type.Title"), Loc.T("Settings.Typing.Method.Type.Detail")),
        new(InjectionPaste, Loc.T("Settings.Typing.Method.Paste.Title"), Loc.T("Settings.Typing.Method.Paste.Detail")),
    ];

    /// <summary>The method a stored value selects; anything but <c>paste</c> is <c>type</c>.</summary>
    public static string NormalizeInjectionMethod(string? stored) =>
        string.Equals(stored?.Trim(), InjectionPaste, StringComparison.OrdinalIgnoreCase) ? InjectionPaste : InjectionType;

    public static TextInjectionMode InjectionModeOf(string? stored) =>
        NormalizeInjectionMethod(stored) == InjectionPaste ? TextInjectionMode.Paste : TextInjectionMode.Type;

    public static IReadOnlyList<SettingsOption> PasteChordOptions =>
    [
        new("Ctrl+V", "Ctrl+V", Loc.T("Settings.Typing.Chord.CtrlV")),
        new("Ctrl+Shift+V", "Ctrl+Shift+V", Loc.T("Settings.Typing.Chord.CtrlShiftV")),
        new("Shift+Insert", "Shift+Insert", Loc.T("Settings.Typing.Chord.ShiftInsert")),
    ];

    /// <summary>The paste shortcut a stored value selects; a hand-edited chord is kept as its own extra option by the page.</summary>
    public static string NormalizePasteChord(string? stored) =>
        string.IsNullOrWhiteSpace(stored) ? "Ctrl+V" : stored.Trim();

    /// <summary>The list for the paste-shortcut radios, with a custom stored chord added so it is not silently lost.</summary>
    public static IReadOnlyList<SettingsOption> PasteChordOptionsFor(string? stored)
    {
        var value = NormalizePasteChord(stored);
        var options = PasteChordOptions;
        return options.Any(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))
            ? options
            : [.. options, new SettingsOption(value, value, Loc.T("Settings.Typing.Chord.Custom"))];
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
    public static IReadOnlyList<SettingsOption> RecognitionLanguages =>
        LanguageCodes.Select(code => code == "auto"
            ? new SettingsOption("auto", Loc.T("Settings.Speech.Language.Auto"))
            : new SettingsOption(code, LanguageNames.Of(code) ?? code)).ToList();

    /// <summary>The recognition language a stored code selects (unknown codes keep their own entry).</summary>
    public static string LanguageTitle(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || string.Equals(code.Trim(), "auto", StringComparison.OrdinalIgnoreCase))
            return Loc.T("Settings.Speech.Language.Auto");
        return RecognitionLanguages.FirstOrDefault(o => string.Equals(o.Value, code.Trim(), StringComparison.OrdinalIgnoreCase))?.Title ?? code.Trim();
    }

    /// <summary>Translation targets: the shared catalog (names, not codes).</summary>
    public static IReadOnlyList<SettingsOption> TranslationTargets =>
        TranslationLanguages.All.Select(l => new SettingsOption(l.Code, LanguageNames.Of(l.Code) ?? l.Name)).ToList();

    /// <summary>The display name of a stored target ("ru" → Russian; a typed name such as Belarusian stays as typed).</summary>
    public static string TargetTitle(string? stored) => LanguageNames.Of(stored) ?? LanguageNames.Of("ru")!;

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
            SettingsSection.Storage => context.StorageLine ?? Loc.T("Settings.Hub.Storage.Default"),
            SettingsSection.Speech => HistoryUiFormat.Join(
                string.IsNullOrWhiteSpace(settings.SelectedModel) ? Loc.T("Settings.Hub.Speech.NoModel") : ModelCatalog.DescribeFolder(settings.SelectedModel),
                ComputeShort(settings.ExecutionProvider),
                IsAuto(settings.Language) ? Loc.T("Settings.Hub.Speech.AutoLanguage") : LanguageTitle(settings.Language)),
            SettingsSection.Audio => AudioSourceTitle(settings.AudioSource, context.Android),
            SettingsSection.Translation => settings.TranslationEnabled
                ? NormalizeEngine(settings.TranslationBackend) == EngineHttp
                    ? Loc.F("Settings.Hub.Translation.Server", TargetTitle(settings.TranslationTargetLanguage))
                    : Loc.F("Settings.Hub.Translation.Device", TargetTitle(settings.TranslationTargetLanguage))
                : Loc.T("Settings.Hub.Off"),
            SettingsSection.Typing => !settings.IsTextInjectionEnabled
                ? Loc.T("Settings.Hub.Off")
                : InjectionModeOf(settings.TextInjectionMethod) == TextInjectionMode.Paste
                    ? Loc.F("Settings.Hub.Typing.Paste", NormalizePasteChord(settings.PasteChord))
                    : Loc.T("Settings.Hub.Typing.Type"),
            SettingsSection.Hotkeys => HotkeyText.Summary(settings),
            _ => GeneralSummary(settings, context.Android),
        };
    }

    /// <summary><c>Window on top · auto-scroll</c>; what is on, in the order of the page.</summary>
    public static string GeneralSummary(AppSettings settings, bool android)
    {
        var parts = new List<string>();
        if (!android && settings.AlwaysOnTop)
            parts.Add(Loc.T("Settings.Hub.General.OnTop"));
        if (settings.IsAutoScrollEnabled)
            parts.Add(Loc.T("Settings.Hub.General.AutoScroll"));
        if (settings.AutoStartRecognition)
            parts.Add(Loc.T("Settings.Hub.General.AutoStart"));
        return parts.Count == 0 ? Loc.T("Settings.Hub.General.AllOff") : string.Join(" · ", parts);
    }

    /// <summary>Subtitle of the History link: <c>Keep audio 30 days · limit 2 GB</c>.</summary>
    public static string HistoryLinkSubtitle(HistorySettings history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var keep = history.AudioRetentionDays <= 0
            ? Loc.T("Settings.Hub.History.KeepForever")
            : Loc.F("Settings.Hub.History.Keep", HistoryUiStorage.RetentionLabel(history.AudioRetentionDays));
        var limit = history.MaxAudioStorageMb > 0
            ? Loc.F("Settings.Hub.History.Limit", HistoryUiStorage.StorageCapLabel(history.MaxAudioStorageMb))
            : Loc.T("Settings.Hub.History.NoLimit");
        return keep + " · " + limit;
    }

    /// <summary>Retention card in Storage › Recordings: <c>Keep audio 30 days · limit 2 GB · Standard quality</c>.</summary>
    public static string RetentionLinkSubtitle(HistorySettings history) =>
        HistoryLinkSubtitle(history) + " · " + QualityName(history.AudioQuality);

    private static string QualityName(AudioQuality quality) => quality switch
    {
        AudioQuality.High => Loc.T("Settings.Hub.History.QualityHigh"),
        AudioQuality.Compact => Loc.T("Settings.Hub.History.QualityCompact"),
        _ => Loc.T("Settings.Hub.History.QualityStandard"),
    };

    public static string ModelsLinkSubtitle => Loc.T("Settings.Hub.ModelsSubtitle");

    // ── Captions ─────────────────────────────────────────────────────────────

    /// <summary>Under the Compute group.</summary>
    public static string ReloadCaption => Loc.T("Settings.Speech.ReloadCaption");

    /// <summary>Shown while a change is waiting for Stop.</summary>
    public static string WaitingForStopCaption => Loc.T("Settings.Speech.WaitingForStopCaption");

    public static string AudioCaptionDesktop => Loc.T("Settings.Audio.CaptionDesktop");

    public static string AudioCaptionAndroid => Loc.T("Settings.Audio.CaptionAndroid");

    public static string ServerUrlHint => Loc.T("Settings.Translation.ServerUrlHint");
    public static string ServerUrlError => Loc.T("Settings.Translation.ServerUrlError");

    /// <summary>An absolute http or https address (what the local server setting needs).</summary>
    public static bool IsValidServerUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>The note under the Typing options for the chosen method.</summary>
    public static string TypingCaptionFor(string? method) =>
        NormalizeInjectionMethod(method) == InjectionPaste
            ? Loc.T("Settings.Typing.CaptionPaste")
            : Loc.T("Settings.Typing.CaptionType");

    public static string ResetSubtitle => Loc.T("Settings.General.ResetSubtitle");

    private static bool IsAuto(string? language) =>
        string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
}
