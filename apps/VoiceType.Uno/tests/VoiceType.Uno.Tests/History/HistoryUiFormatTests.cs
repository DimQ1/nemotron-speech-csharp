using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryUiFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 0, 0, TimeSpan.FromHours(3));

    private static RecordingManifest Manifest(DateTimeOffset startedAt, double durationSec = 754.2, ModelInfo? model = null) => new()
    {
        Id = "7f3a9c",
        StartedAt = startedAt,
        Audio = new AudioInfo { File = "audio.ogg", DurationSec = durationSec },
        CurrentVersion = "v1",
        Versions =
        [
            new TranscriptVersion
            {
                Id = "v1",
                Model = model ?? new ModelInfo { Title = "Parakeet TDT 0.6B v3 — full precision (FP32)", Variant = "FP32", Folder = "parakeet-fp32" },
                Language = new LanguageInfo { Requested = "auto", Detected = "en" },
            },
        ],
    };

    [Theory]
    [InlineData(48, "0:48")]
    [InlineData(754.2, "12:34")]
    [InlineData(5045, "1:24:05")]
    [InlineData(-3, "0:00")]
    public void Duration_ShouldUseMinutesAndHours(double seconds, string expected) =>
        Assert.Equal(expected, HistoryUiFormat.Duration(seconds));

    [Fact]
    public void DurationSeconds_ShouldFallBackToWallTimeWithoutAudio()
    {
        var m = new RecordingManifest { StartedAt = Now, EndedAt = Now.AddSeconds(90) };
        Assert.Equal(90, HistoryUiFormat.DurationSeconds(m));
        Assert.Equal(0, HistoryUiFormat.DurationSeconds(new RecordingManifest { StartedAt = Now }));
    }

    [Theory]
    [InlineData("Parakeet TDT 0.6B v3 — full precision (FP32)", "FP32", null, "Parakeet FP32")]
    [InlineData("Nemotron 3.5 ASR — 4-bit (INT4)", null, "Int4", "Nemotron INT4")]
    [InlineData(null, null, null, "my-folder")]
    public void ShortModel_ShouldCombineFamilyAndVariant(string? title, string? variant, string? precision, string expected) =>
        Assert.Equal(expected, HistoryUiFormat.ShortModel(new ModelInfo { Title = title, Variant = variant, Precision = precision, Folder = "my-folder" }));

    [Fact]
    public void LanguageCode_ShouldPreferDetectedAndSkipAuto()
    {
        Assert.Equal("EN", HistoryUiFormat.LanguageCode(new LanguageInfo { Requested = "auto", Detected = "en" }));
        Assert.Equal("RU", HistoryUiFormat.LanguageCode(new LanguageInfo { Requested = "ru" }));
        Assert.Equal("", HistoryUiFormat.LanguageCode(new LanguageInfo { Requested = "auto" }));
        Assert.Equal("Auto → EN", HistoryUiFormat.LanguageDetail(new LanguageInfo { Requested = "auto", Detected = "en" }));
        Assert.Equal("RU", HistoryUiFormat.LanguageDetail(new LanguageInfo { Requested = "ru", Detected = "ru" }));
    }

    [Fact]
    public void RowMeta_ShouldShowTimeForTodayAndDateForEarlier()
    {
        Assert.Equal("14:02 · 12:34 · Parakeet FP32 · EN",
            HistoryUiFormat.RowMeta(Manifest(new DateTimeOffset(2026, 10, 12, 14, 2, 11, TimeSpan.FromHours(3))), Now));
        Assert.Equal("Sep 28 · 1:24:05 · Parakeet FP32 · EN",
            HistoryUiFormat.RowMeta(Manifest(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(3)), 5045), Now));
    }

    [Fact]
    public void RowMeta_ShouldConvertToTheLocalOffset()
    {
        // 11:02 UTC is 14:02 at +03:00.
        Assert.StartsWith("14:02 ·", HistoryUiFormat.RowMeta(Manifest(new DateTimeOffset(2026, 10, 12, 11, 2, 0, TimeSpan.Zero)), Now));
    }

    [Fact]
    public void HeaderSubtitle_ShouldShowWeekdayDateTimeDurationLanguage() =>
        Assert.Equal("Mon 12 Oct, 14:02 · 12:34 · EN",
            HistoryUiFormat.HeaderSubtitle(Manifest(new DateTimeOffset(2026, 10, 12, 14, 2, 11, TimeSpan.FromHours(3))), Now));

    [Fact]
    public void VersionLabels_ShouldDescribeKindAndModel()
    {
        var live = new TranscriptVersion { Id = "v1", Kind = VersionKinds.Live, Model = new ModelInfo { Title = "Parakeet TDT", Variant = "INT4" } };
        var edited = new TranscriptVersion { Id = "v3", Kind = VersionKinds.Edited, Parent = "v2", Model = live.Model };
        var bare = new TranscriptVersion { Id = "v1", Kind = VersionKinds.Live };

        Assert.Equal("v1 · Parakeet INT4", HistoryUiFormat.VersionShort(live));
        Assert.Equal("v3 · Edited", HistoryUiFormat.VersionShort(edited));
        Assert.Equal("v1 · Live", HistoryUiFormat.VersionShort(bare));
        Assert.Equal("Recorded live", HistoryUiFormat.VersionKind(live));
        Assert.Equal("Edited from v2", HistoryUiFormat.VersionKind(edited));
        Assert.Equal("Re-recognized", HistoryUiFormat.VersionKind(new TranscriptVersion { Kind = VersionKinds.Rerecognized }));
        Assert.Equal("v1 · Unknown model", HistoryUiFormat.VersionTitle(bare));
        Assert.Equal("v1 · Live", HistoryUiFormat.VersionTiny(live));
        Assert.Equal("v3 · Edited", HistoryUiFormat.VersionTiny(edited));
        Assert.Equal("v2 · FP32", HistoryUiFormat.VersionTiny(new TranscriptVersion
        {
            Id = "v2", Kind = VersionKinds.Rerecognized, Model = new ModelInfo { Variant = "FP32" },
        }));
    }

    [Fact]
    public void Vad_ShouldReadTheSettingsFlag()
    {
        var on = new TranscriptVersion { Settings = JsonValues.Settings(("useVad", true)) };
        var off = new TranscriptVersion { Settings = JsonValues.Settings(("useVad", false)) };
        Assert.Equal("on", HistoryUiFormat.Vad(on));
        Assert.Equal("off", HistoryUiFormat.Vad(off));
        Assert.Equal("", HistoryUiFormat.Vad(new TranscriptVersion()));
    }

    [Theory]
    [InlineData(0, "0 MB")]
    [InlineData(1000, "1 KB")]
    [InlineData(43_000_000, "41 MB")]
    [InlineData(2_400_000, "2.3 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    public void Bytes_ShouldUseBinaryUnits(long bytes, string expected) =>
        Assert.Equal(expected, HistoryUiFormat.Bytes(bytes));

    [Fact]
    public void ListSubtitle_ShouldCountRecordingsAndSize()
    {
        Assert.Equal("No recordings yet", HistoryUiFormat.ListSubtitle(0, 0));
        Assert.Equal("1 recording · 1 KB", HistoryUiFormat.ListSubtitle(1, 1024));
        Assert.Equal("24 recordings · 1.2 GB", HistoryUiFormat.ListSubtitle(24, 1_288_490_189));
    }
}
