using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.Settings;

public class SettingsTextTests
{
    [Theory]
    [InlineData("cpu", "Processor (CPU)")]
    [InlineData("webgpu:hp", "Graphics card — performance")]
    [InlineData("webgpu:lp", "Graphics card — power saving")]
    [InlineData("follow_config", "As set by the model")]
    public void ComputeTitle_ShouldMapStoredValuesToNames(string stored, string title)
        => Assert.Equal(title, SettingsText.ComputeTitle(stored));

    [Theory]
    [InlineData("cuda")]
    [InlineData("dml")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeCompute_ShouldShowProcessor_ForStaleOrEmptyValues(string? stored)
        => Assert.Equal("cpu", SettingsText.NormalizeCompute(stored));

    [Fact]
    public void ComputeOptions_ShouldStoreTheDocumentedValues_AndMarkTheProcessorRecommended()
    {
        Assert.Equal(["cpu", "webgpu:hp", "webgpu:lp", "follow_config"], SettingsText.ComputeOptions.Select(o => o.Value));
        Assert.Equal("Recommended", SettingsText.ComputeOptions[0].Tag);
    }

    [Fact]
    public void ComputeOptions_AsSetByTheModel_ShouldSayItRunsOnTheProcessor_NotPromiseTheProviderOfTheModelConfig()
    {
        var detail = SettingsText.ComputeOptions.Single(o => o.Value == SettingsText.ComputeFollowModel).Detail;

        Assert.Contains("processor", detail, StringComparison.OrdinalIgnoreCase);   // the recognizers map follow_config to the CPU
        Assert.DoesNotContain("genai_config", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComputeOptions_GraphicsCard_ShouldSayWhatNemotronDoesWithIt()
    {
        // Nemotron: WebGPU runs the encoder on a discrete card only; the power-saving choice (integrated graphics) is the processor
        var performance = SettingsText.ComputeOptions.Single(o => o.Value == SettingsText.ComputeGpuPerformance).Detail!;
        var powerSaving = SettingsText.ComputeOptions.Single(o => o.Value == SettingsText.ComputeGpuPowerSaving).Detail!;

        Assert.Contains("discrete", performance);
        Assert.Contains("encoder", performance);
        Assert.Contains("without a discrete card it runs on the processor", performance);
        Assert.Contains("Nemotron does not use it", powerSaving);
        Assert.Contains("processor", powerSaving);
    }

    [Fact]
    public void ComputeShort_AsSetByTheModel_ShouldNameTheProcessorLikeTheDetailDoes()
    {
        // the section subtitle must not say something else than the option's own explanation
        Assert.Equal("CPU (by model)", SettingsText.ComputeShort(SettingsText.ComputeFollowModel));
        Assert.Equal("CPU", SettingsText.ComputeShort(SettingsText.ComputeCpu));
        Assert.Equal("GPU", SettingsText.ComputeShort(SettingsText.ComputeGpuPerformance));
    }

    [Theory]
    [InlineData("Mic", false, "Microphone")]
    [InlineData("Loopback", false, "System audio")]
    [InlineData("Mix", false, "Microphone + system audio")]
    [InlineData("mix", false, "Microphone + system audio")]
    public void AudioSourceTitle_ShouldMapStoredValuesToNames(string stored, bool android, string title)
        => Assert.Equal(title, SettingsText.AudioSourceTitle(stored, android));

    [Fact]
    public void AudioSourceOptions_OnAndroid_ShouldOfferOnlyTheMicrophone()
    {
        var options = SettingsText.AudioSourceOptions(android: true);

        Assert.Equal(["Mic", "Loopback"], options.Select(o => o.Value));
        Assert.True(options[0].Enabled);
        Assert.False(options[1].Enabled);
        Assert.Contains("Not available on Android", options[1].Detail);
    }

    [Theory]
    [InlineData("Loopback")]
    [InlineData("Mix")]
    [InlineData("nonsense")]
    public void NormalizeAudioSource_OnAndroid_ShouldFallBackToTheMicrophone(string stored)
        => Assert.Equal("Mic", SettingsText.NormalizeAudioSource(stored, android: true));

    [Theory]
    [InlineData("Mic", "Microphone", AudioSourceGlyph.Microphone)]
    [InlineData("Loopback", "System audio", AudioSourceGlyph.SystemAudio)]
    [InlineData("Mix", "Mic + system audio", AudioSourceGlyph.Both)]
    [InlineData("loopback", "System audio", AudioSourceGlyph.SystemAudio)]
    [InlineData("", "Microphone", AudioSourceGlyph.Microphone)]
    [InlineData(null, "Microphone", AudioSourceGlyph.Microphone)]
    public void AudioSourceChip_ShouldNameAndIconTheSourceOnTheDesktop(string? stored, string chipTitle, AudioSourceGlyph glyph)
    {
        Assert.Equal(chipTitle, SettingsText.AudioSourceChipTitle(stored, android: false));
        Assert.Equal(glyph, SettingsText.AudioSourceGlyphOf(stored, android: false));
    }

    [Theory]
    [InlineData("Loopback")]
    [InlineData("Mix")]
    public void AudioSourceChip_OnAndroid_ShouldShowTheMicrophoneWhateverIsStored(string stored)
    {
        Assert.Equal("Microphone", SettingsText.AudioSourceChipTitle(stored, android: true));
        Assert.Equal(AudioSourceGlyph.Microphone, SettingsText.AudioSourceGlyphOf(stored, android: true));
    }

    [Fact]
    public void HasAudioSourceChoice_ShouldBeTrueOnlyWhereMoreThanOneSourceWorks()
    {
        Assert.True(SettingsText.HasAudioSourceChoice(android: false));
        Assert.False(SettingsText.HasAudioSourceChoice(android: true));
    }

    [Fact]
    public void AudioSourceChipName_ShouldSaySourceAndThatItCanBeChanged()
    {
        Assert.Equal("Audio source: System audio. Tap to change.", SettingsText.AudioSourceChipName("Loopback", android: false));
        Assert.Equal("Audio source: Microphone", SettingsText.AudioSourceChipName("Mic", android: true));
    }

    [Theory]
    [InlineData("native", "On this device")]
    [InlineData("http", "Local server")]
    [InlineData("HTTP", "Local server")]
    [InlineData("", "On this device")]
    public void EngineTitle_ShouldMapNativeAndHttp(string stored, string title)
        => Assert.Equal(title, SettingsText.EngineTitle(stored));

    [Theory]
    [InlineData("cpu", "cpu")]
    [InlineData("gpu", "gpu")]
    [InlineData("GPU", "gpu")]
    [InlineData("", "cpu")]
    public void NormalizeGemmaCompute_ShouldKeepCpuAndGpu(string stored, string expected)
        => Assert.Equal(expected, SettingsText.NormalizeGemmaCompute(stored));

    [Fact]
    public void PasteChordOptionsFor_ShouldKeepAHandEditedChordAsItsOwnOption()
    {
        var plain = SettingsText.PasteChordOptionsFor("Ctrl+V");
        var custom = SettingsText.PasteChordOptionsFor("Ctrl+Alt+V");

        Assert.Equal(3, plain.Count);
        Assert.Equal(4, custom.Count);
        Assert.Equal("Ctrl+Alt+V", custom[^1].Value);
    }

    [Theory]
    [InlineData("type", "type")]
    [InlineData("paste", "paste")]
    [InlineData("PASTE", "paste")]
    [InlineData(" paste ", "paste")]
    [InlineData("", "type")]
    [InlineData(null, "type")]
    [InlineData("clipboard", "type")]
    public void NormalizeInjectionMethod_ShouldBePasteOnlyForPaste(string? stored, string expected)
    {
        Assert.Equal(expected, SettingsText.NormalizeInjectionMethod(stored));
        Assert.Equal(expected == "paste" ? TextInjectionMode.Paste : TextInjectionMode.Type, SettingsText.InjectionModeOf(stored));
    }

    [Fact]
    public void AppSettings_ShouldTypeByDefault_AndOldSettingsFilesStayTyping()
    {
        Assert.Equal("type", new AppSettings().TextInjectionMethod);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "PasteChord": "Shift+Insert" }""")!;
        Assert.Equal(TextInjectionMode.Type, SettingsText.InjectionModeOf(old.TextInjectionMethod));
    }

    [Fact]
    public void InjectionMethodOptions_ShouldOfferTypeAndPaste()
    {
        Assert.Equal(["type", "paste"], SettingsText.InjectionMethodOptions.Select(o => o.Value));
    }

    [Fact]
    public void TypingCaptionFor_ShouldDescribeTheChosenMethod()
    {
        Assert.Contains("types the phrase key by key", SettingsText.TypingCaptionFor("type"));
        Assert.Contains("stays on the clipboard", SettingsText.TypingCaptionFor("paste"));
        Assert.Equal(SettingsText.TypingCaptionFor("type"), SettingsText.TypingCaptionFor("nonsense"));
    }

    [Fact]
    public void NearestRepetition_ShouldSnapToTheOfferedValues()
    {
        Assert.Equal(1.1, SettingsText.NearestRepetition(1.1));
        Assert.Equal(1.2, SettingsText.NearestRepetition(1.25));
        Assert.Equal(1.5, SettingsText.NearestRepetition(3));
        Assert.Equal(1.0, SettingsText.NearestRepetition(0.2));
    }

    [Fact]
    public void LanguageTitle_ShouldUseHumanNames()
    {
        Assert.Equal("Auto-detect", SettingsText.LanguageTitle("auto"));
        Assert.Equal("Auto-detect", SettingsText.LanguageTitle(""));
        Assert.Equal("German", SettingsText.LanguageTitle("de"));
        Assert.Equal("xx", SettingsText.LanguageTitle("xx"));
    }

    [Fact]
    public void TargetTitle_ShouldNameCodes_AndKeepTypedNames()
    {
        Assert.Equal("Russian", SettingsText.TargetTitle("ru"));
        Assert.Equal("Belarusian", SettingsText.TargetTitle("Belarusian"));
        Assert.False(SettingsText.IsCustomTarget("ru"));
        Assert.False(SettingsText.IsCustomTarget("German"));
        Assert.True(SettingsText.IsCustomTarget("Belarusian"));
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    [Fact]
    public void VisibleSections_OnDesktop_ShouldListAllSevenInOrder()
        => Assert.Equal(
            [SettingsSection.Storage, SettingsSection.Speech, SettingsSection.Audio, SettingsSection.Translation, SettingsSection.Typing, SettingsSection.Hotkeys, SettingsSection.General],
            SettingsText.VisibleSections(android: false));

    [Fact]
    public void VisibleSections_OnAndroid_ShouldHideTypingAndShortcuts()
        => Assert.Equal(
            [SettingsSection.Storage, SettingsSection.Speech, SettingsSection.Audio, SettingsSection.Translation, SettingsSection.General],
            SettingsText.VisibleSections(android: true));

    [Theory]
    [InlineData(839.9, false)]
    [InlineData(840, true)]
    [InlineData(1280, true)]
    [InlineData(360, false)]
    public void IsWide_ShouldSwitchAt840(double width, bool wide)
        => Assert.Equal(wide, SettingsText.IsWide(width));

    [Fact]
    public void InitialSection_ShouldBeStorageOnWideAndTheHubOnNarrow()
    {
        Assert.Equal(SettingsSection.Storage, SettingsText.InitialSection(wide: true));
        Assert.Null(SettingsText.InitialSection(wide: false));
    }

    [Fact]
    public void Coerce_ShouldFallBackToTheFirstVisibleSection()
    {
        Assert.Equal(SettingsSection.Storage, SettingsText.Coerce(SettingsSection.Hotkeys, android: true));
        Assert.Equal(SettingsSection.Hotkeys, SettingsText.Coerce(SettingsSection.Hotkeys, android: false));
    }

    [Fact]
    public void Header_ShouldShowTheSectionOnNarrowAndSettingsOnWide()
    {
        Assert.Equal("Settings", SettingsText.HeaderTitle(null, wide: false));
        Assert.Equal("Audio input", SettingsText.HeaderTitle(SettingsSection.Audio, wide: false));
        Assert.Equal("Settings", SettingsText.HeaderSubtitle(SettingsSection.Audio, wide: false));
        Assert.Equal("Settings", SettingsText.HeaderTitle(SettingsSection.Audio, wide: true));
        Assert.Equal("VoiceType", SettingsText.HeaderSubtitle(SettingsSection.Audio, wide: true));
    }

    // ── Subtitles ────────────────────────────────────────────────────────────

    [Fact]
    public void Subtitle_Speech_ShouldNameTheModelComputeAndLanguage()
    {
        var settings = new AppSettings { SelectedModel = "custom-model", ExecutionProvider = "webgpu:lp", Language = "de" };

        Assert.Equal("custom-model · GPU (power saving) · German", SettingsText.Subtitle(SettingsSection.Speech, settings, new SettingsText.SubtitleContext(false)));
    }

    [Fact]
    public void Subtitle_Speech_WithoutModel_ShouldSaySo()
        => Assert.StartsWith("No model selected · CPU · auto language",
            SettingsText.Subtitle(SettingsSection.Speech, new AppSettings(), new SettingsText.SubtitleContext(false)));

    [Fact]
    public void Subtitle_Translation_ShouldReflectSwitchTargetAndEngine()
    {
        var off = new AppSettings { TranslationEnabled = false };
        var native = new AppSettings { TranslationEnabled = true, TranslationTargetLanguage = "ru" };
        var http = new AppSettings { TranslationEnabled = true, TranslationTargetLanguage = "de", TranslationBackend = "http" };
        var context = new SettingsText.SubtitleContext(false);

        Assert.Equal("Off", SettingsText.Subtitle(SettingsSection.Translation, off, context));
        Assert.Equal("Into Russian · Gemma on this device", SettingsText.Subtitle(SettingsSection.Translation, native, context));
        Assert.Equal("Into German · Gemma on a local server", SettingsText.Subtitle(SettingsSection.Translation, http, context));
    }

    [Fact]
    public void Subtitle_Typing_AndHotkeys_AndAudio()
    {
        var settings = new AppSettings { IsTextInjectionEnabled = true, PasteChord = "Ctrl+Shift+V", ToggleHotkey = "Ctrl+Alt+R", AudioSource = "Mix" };
        var context = new SettingsText.SubtitleContext(false);

        Assert.Equal("On · typed as key presses", SettingsText.Subtitle(SettingsSection.Typing, settings, context));
        settings.TextInjectionMethod = "paste";
        Assert.Equal("On · paste with Ctrl+Shift+V", SettingsText.Subtitle(SettingsSection.Typing, settings, context));
        Assert.Equal("Ctrl+Alt+R starts and stops", SettingsText.Subtitle(SettingsSection.Hotkeys, settings, context));
        Assert.Equal("Microphone + system audio", SettingsText.Subtitle(SettingsSection.Audio, settings, context));
        settings.IsTextInjectionEnabled = false;
        settings.ToggleHotkey = "";
        Assert.Equal("Off", SettingsText.Subtitle(SettingsSection.Typing, settings, context));
        Assert.Equal("No shortcut to start and stop", SettingsText.Subtitle(SettingsSection.Hotkeys, settings, context));
    }

    [Fact]
    public void GeneralSummary_ShouldListWhatIsOn_AndSkipWindowOnTopOnAndroid()
    {
        var settings = new AppSettings { AlwaysOnTop = true, IsAutoScrollEnabled = true, AutoStartRecognition = true };

        Assert.Equal("Window on top · auto-scroll · auto-start", SettingsText.GeneralSummary(settings, android: false));
        Assert.Equal("auto-scroll · auto-start", SettingsText.GeneralSummary(settings, android: true));
        Assert.Equal("Everything off", SettingsText.GeneralSummary(new AppSettings { AlwaysOnTop = false, IsAutoScrollEnabled = false }, android: false));
    }

    [Fact]
    public void Subtitle_Storage_UsesTheMeasuredLine_OrAGenericOne()
    {
        Assert.Equal("Models 1 GB · recordings 2 GB", SettingsText.Subtitle(SettingsSection.Storage, new AppSettings(), new SettingsText.SubtitleContext(false, "Models 1 GB · recordings 2 GB")));
        Assert.Equal("Models, recordings and app data", SettingsText.Subtitle(SettingsSection.Storage, new AppSettings(), new SettingsText.SubtitleContext(false)));
    }

    [Fact]
    public void HistoryLinkSubtitle_ShouldDescribeRetentionAndLimit()
    {
        var history = new HistorySettings { AudioRetentionDays = 30, MaxAudioStorageMb = 2048 };

        Assert.Equal("Keep audio 30 days · limit 2 GB", SettingsText.HistoryLinkSubtitle(history));
        Assert.Equal("Keep audio 30 days · limit 2 GB · Standard quality", SettingsText.RetentionLinkSubtitle(history));
        Assert.Equal("Keep audio forever · no limit", SettingsText.HistoryLinkSubtitle(new HistorySettings { AudioRetentionDays = 0, MaxAudioStorageMb = 0 }));
    }

    [Theory]
    [InlineData("http://localhost:9379", true)]
    [InlineData(" https://example.com/v1 ", true)]
    [InlineData("localhost:9379", false)]
    [InlineData("ftp://host", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidServerUrl_ShouldNeedAnAbsoluteHttpAddress(string? text, bool valid)
        => Assert.Equal(valid, SettingsText.IsValidServerUrl(text));

    [Fact]
    public void LanguageCodes_ShouldStartWithAutoAndMatchTheNamedList()
    {
        Assert.Equal("auto", SettingsText.LanguageCodes[0]);
        Assert.Equal(SettingsText.LanguageCodes, SettingsText.RecognitionLanguages.Select(o => o.Value));
        Assert.All(SettingsText.RecognitionLanguages.Skip(1), o => Assert.NotEqual(o.Value, o.Title));
    }

    // ── Reset ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_ShouldRestoreOptions_AndKeepFoldersModelsAndHistory()
    {
        var current = new AppSettings
        {
            ModelsRootPath = @"D:\Models", SessionsPath = @"D:\Rec", PendingSessionsPath = @"E:\Rec", SelectedModel = "m", ModelPath = @"D:\Models\asr\m",
            ExecutionProvider = "webgpu:hp", Language = "de", AudioSource = "Mix", UseVad = false, RepetitionPenalty = 1.5,
            ToggleHotkey = "Ctrl+Alt+R", TranslationEnabled = true, TranslationBackend = "http", AlwaysOnTop = false, AutoStartRecognition = true,
            TextInjectionMethod = "paste", PasteChord = "Shift+Insert",
        };
        current.History.AudioRetentionDays = 7;

        var fresh = SettingsDefaults.Reset(current);

        Assert.Equal("cpu", fresh.ExecutionProvider);
        Assert.Equal("auto", fresh.Language);
        Assert.Equal("Mic", fresh.AudioSource);
        Assert.True(fresh.UseVad);
        Assert.Equal("Ctrl+Shift+V", fresh.ToggleHotkey);
        Assert.False(fresh.TranslationEnabled);
        Assert.Equal("native", fresh.TranslationBackend);
        Assert.Equal("type", fresh.TextInjectionMethod);
        Assert.Equal("Ctrl+V", fresh.PasteChord);
        Assert.True(fresh.AlwaysOnTop);
        Assert.False(fresh.AutoStartRecognition);
        Assert.Equal(@"D:\Models", fresh.ModelsRootPath);
        Assert.Equal(@"D:\Rec", fresh.SessionsPath);
        Assert.Equal(@"E:\Rec", fresh.PendingSessionsPath);
        Assert.Equal("m", fresh.SelectedModel);
        Assert.Equal(7, fresh.History.AudioRetentionDays);
        Assert.NotSame(current.History, fresh.History);
    }
}
