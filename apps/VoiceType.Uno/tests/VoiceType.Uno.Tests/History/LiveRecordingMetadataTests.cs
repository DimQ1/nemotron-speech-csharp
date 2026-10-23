using VoiceType.Uno.History;
using Xunit;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Tests.History;

public class LiveRecordingMetadataTests
{
    [Theory]
    [InlineData(true, false, false, true)]   // history on, plain dictation
    [InlineData(true, true, true, true)]     // inject on, inject dictations saved
    [InlineData(true, true, false, false)]   // inject on, inject dictations excluded
    [InlineData(false, false, true, false)]  // history off
    public void Decide_ShouldFollowHistorySettingsAndModesAtStart(
        bool enabled, bool inject, bool saveInject, bool expectedSave)
    {
        var settings = new HistorySettings { Enabled = enabled, SaveInjectDictations = saveInject };

        var plan = LiveRecordingPlan.Decide(settings, injectText: inject, translation: true);

        Assert.Equal(expectedSave, plan.Save);
        Assert.Equal(inject, plan.Mode.InjectText);
        Assert.True(plan.Mode.Translation);
    }

    [Theory]
    [InlineData(AudioQuality.Standard, "opus:48", "opus", 48000, "audio.ogg")]
    [InlineData(AudioQuality.High, "wav", "pcm16", null, "audio.wav")]
    [InlineData(AudioQuality.Compact, "opus:24", "opus", 24000, "audio.ogg")]
    public void AudioFormats_ShouldMapQuality(AudioQuality quality, string spec, string codec, int? bitrate, string file)
    {
        Assert.Equal(spec, HistoryAudioFormats.Spec(quality));
        Assert.Equal(codec, HistoryAudioFormats.Codec(quality));
        Assert.Equal(bitrate, HistoryAudioFormats.BitrateBps(quality));
        Assert.Equal(file, HistoryAudioFormats.FileName(quality));
    }

    [Fact]
    public void DescribeModel_ShouldUseTheCatalogForKnownFolders()
    {
        var descriptor = SpeechLib.ModelDownload.ModelCatalog.Recommended;
        var path = Path.Combine("models", descriptor.SubfolderName);

        var model = LiveRecordingMetadata.DescribeModel(path, "ParakeetTdt");

        Assert.Equal(descriptor.RepoId, model.CatalogRepoId);
        Assert.Equal(descriptor.SubfolderName, model.Folder);
        Assert.Equal(descriptor.Title, model.Title);
        Assert.Equal(descriptor.Precision.ToString(), model.Precision);
        Assert.Equal(descriptor.Variant, model.Variant);
        Assert.Equal("ParakeetTdt", model.Family);
    }

    [Fact]
    public void DescribeModel_ShouldFallBackForFoldersOutsideTheCatalog()
    {
        var model = LiveRecordingMetadata.DescribeModel(
            Path.Combine("models", "nemotron-asr-int4-cpu") + Path.DirectorySeparatorChar,
            "Nemotron", "owner/nemotron-asr-int4-cpu", "Nemotron INT4");

        Assert.Equal("owner/nemotron-asr-int4-cpu", model.CatalogRepoId);
        Assert.Equal("nemotron-asr-int4-cpu", model.Folder);
        Assert.Equal("Nemotron INT4", model.Title);
        Assert.Equal("Int4", model.Precision);
        Assert.Null(model.Variant);
    }

    [Fact]
    public void BuildLiveVersion_ShouldRecordModelLanguageSettingsAndApp()
    {
        var settings = new AppSettings
        {
            Language = "auto",
            UseVad = false,
            RepetitionPenalty = 1.2,
            ExecutionProvider = "cpu",
            AudioSource = "Mix",
        };
        var recognizer = new LiveRecognizerInfo("m", "ParakeetTdt", 16000, true, 0.8,
            [new("chunkSec", 0.32), new("stopHistoryEouSec", 0.8), new("useVad", true)]);
        var app = new AppInfo { Name = LiveRecordingMetadata.AppName, Version = "1.0", Platform = "win-x64" };

        var version = LiveRecordingMetadata.BuildLiveVersion(
            settings, recognizer, new ModelInfo { Folder = "m" }, "en", app);

        Assert.Equal(VersionKinds.Live, version.Kind);
        Assert.Equal("auto", version.Language!.Requested);
        Assert.Equal("en", version.Language.Detected);
        Assert.Equal("m", version.Model!.Folder);
        Assert.Same(app, version.App);
        var s = version.Settings!;
        Assert.False(s["useVad"].GetBoolean());            // app setting wins over a recognizer duplicate
        Assert.Equal(1.2, s["repetitionPenalty"].GetDouble());
        Assert.Equal("cpu", s["executionProvider"].GetString());
        Assert.Equal("Mix", s["audioSource"].GetString());
        Assert.Equal(16000, s["sampleRate"].GetInt32());
        Assert.Equal(0.32, s["chunkSec"].GetDouble());
        Assert.Equal(0.8, s["stopHistoryEouSec"].GetDouble());
    }

    [Fact]
    public void BuildAudioInfo_ShouldConvertSamplesAndGapsToSeconds()
    {
        var audio = LiveRecordingMetadata.BuildAudioInfo(
            "audio.ogg", AudioQuality.Standard, 16000, samples: 160_000, bytes: 12_345, source: "Mic",
            gaps: [(48_000, 32_000), (64_000, 0)]);

        Assert.Equal("audio.ogg", audio.File);
        Assert.Equal("opus", audio.Codec);
        Assert.Equal(48000, audio.Bitrate);
        Assert.Equal(10.0, audio.DurationSec);
        Assert.Equal(12_345, audio.Bytes);
        Assert.Equal("Mic", audio.Source);
        var gap = Assert.Single(audio.Gaps);
        Assert.Equal(3.0, gap.AtSec);
        Assert.Equal(2.0, gap.WallSec);
        Assert.Equal("muted", gap.Reason);
    }

    [Fact]
    public void BuildAudioInfo_ShouldListTheSourceSwitchesOfARecordingInSeconds()
    {
        var audio = LiveRecordingMetadata.BuildAudioInfo(
            "audio.ogg", AudioQuality.Standard, 16000, samples: 160_000, bytes: 1, source: "Mic",
            gaps: [], sourceChanges: [(48_000, "Loopback"), (112_000, "Mix")]);

        Assert.Equal("Mic", audio.Source);   // what the recording started with
        Assert.NotNull(audio.SourceChanges);
        Assert.Equal([(3.0, "Loopback"), (7.0, "Mix")], audio.SourceChanges.Select(c => (c.AtSec, c.Source)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildAudioInfo_WithoutSourceSwitches_ShouldLeaveThemOutOfTheManifest(bool emptyList)
    {
        var audio = LiveRecordingMetadata.BuildAudioInfo(
            "audio.ogg", AudioQuality.Standard, 16000, samples: 16_000, bytes: 1, source: "Mic",
            gaps: [], sourceChanges: emptyList ? [] : null);

        Assert.Null(audio.SourceChanges);   // null is not written: manifests of recordings that never switched stay as they were
    }

    [Theory]
    [InlineData("hello", null, 0.5, true)]
    [InlineData("", "typed", 0.5, true)]
    [InlineData("", null, 5.0, true)]
    [InlineData(" ", null, 1.0, false)]
    public void ShouldKeep_ShouldDropOnlyEmptyShortSessions(string recognized, string? edited, double seconds, bool expected) =>
        Assert.Equal(expected, LiveRecordingMetadata.ShouldKeep(recognized, edited, seconds));

    [Fact]
    public void DetectLiveEdit_ShouldIgnoreUntouchedSessions() =>
        Assert.Null(LiveRecordingMetadata.DetectLiveEdit("", "something else", "hello", userTouched: false));

    [Fact]
    public void DetectLiveEdit_ShouldReturnTheSessionPartAfterKeptText()
    {
        var edited = LiveRecordingMetadata.DetectLiveEdit(
            "Earlier note. ", "Earlier note. Hello brave world", "hello world", userTouched: true);

        Assert.Equal("Hello brave world", edited);
    }

    [Fact]
    public void DetectLiveEdit_ShouldIgnoreWhitespaceOnlyDifferences() =>
        Assert.Null(LiveRecordingMetadata.DetectLiveEdit("", " hello   world ", "hello world", userTouched: true));

    [Fact]
    public void DetectLiveEdit_ShouldIgnoreAClearedField() =>
        Assert.Null(LiveRecordingMetadata.DetectLiveEdit("", "", "hello world", userTouched: true));

    [Fact]
    public void LiveEditExtension_ShouldMarkTheSourceAsLive() =>
        Assert.Equal("live", LiveRecordingMetadata.LiveEditExtension()["edit"].GetProperty("source").GetString());

    [Theory]
    [InlineData("1.0.3+abc123", "1.0.3")]
    [InlineData("2.1", "2.1")]
    [InlineData(null, null)]
    public void CleanVersion_ShouldDropTheSourceRevision(string? input, string? expected) =>
        Assert.Equal(expected, LiveRecordingMetadata.CleanVersion(input));
}
