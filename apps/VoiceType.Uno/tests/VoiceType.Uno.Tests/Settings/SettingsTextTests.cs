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
