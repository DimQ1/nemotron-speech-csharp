using VoiceType.Uno.History;
using VoiceType.Uno.Services;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistorySettingsTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void Defaults_ShouldMatchTheDecisions()
    {
        var history = new AppSettings().History;

        Assert.True(history.Enabled);
        Assert.Equal(30, history.AudioRetentionDays);
        Assert.Equal(2048, history.MaxAudioStorageMb);
        Assert.True(history.ConfirmDelete);
        Assert.True(history.SaveInjectDictations);
        Assert.Equal(AudioQuality.Standard, history.AudioQuality);
        Assert.Contains(0, HistorySettings.RetentionDayChoices);
    }

    [Fact]
    public void Load_ShouldAcceptOldSettingsWithoutHistorySection()
    {
        _directory.Write("settings.json", """{ "Language": "ru", "UseVad": false }""");

        var settings = new SettingsService(Path.Combine(_directory.Path, "settings.json")).Load();

        Assert.Equal("ru", settings.Language);
        Assert.Equal(30, settings.History.AudioRetentionDays);
    }

    [Fact]
    public void Load_ShouldTreatNullHistoryAndUnknownQualityAsDefaults()
    {
        _directory.Write("a.json", """{ "Language": "de", "History": null }""");
        _directory.Write("b.json", """{ "Language": "de", "History": { "AudioQuality": "Lossless", "AudioRetentionDays": 0 } }""");

        var a = new SettingsService(Path.Combine(_directory.Path, "a.json")).Load();
        var b = new SettingsService(Path.Combine(_directory.Path, "b.json")).Load();

        Assert.NotNull(a.History);
        Assert.Equal("de", b.Language);
        Assert.Equal(AudioQuality.Standard, b.History.AudioQuality);
        Assert.True(b.History.KeepAudioForever);
    }

    [Fact]
    public void SaveAndLoad_ShouldRoundTripHistorySection()
    {
        var path = Path.Combine(_directory.Path, "settings.json");
        var service = new SettingsService(path);
        var settings = new AppSettings();
        settings.History.AudioRetentionDays = 365;
        settings.History.MaxAudioStorageMb = 0;
        settings.History.ConfirmDelete = false;
        settings.History.SaveInjectDictations = false;
        settings.History.AudioQuality = AudioQuality.High;

        service.Save(settings);
        var loaded = service.Load();

        Assert.Equal(365, loaded.History.AudioRetentionDays);
        Assert.Equal(0, loaded.History.MaxAudioStorageBytes);
        Assert.False(loaded.History.ConfirmDelete);
        Assert.False(loaded.History.SaveInjectDictations);
        Assert.Equal(AudioQuality.High, loaded.History.AudioQuality);
        Assert.Contains("\"AudioQuality\": \"high\"", File.ReadAllText(path));
    }

    [Fact]
    public void Clone_ShouldDeepCopyHistory()
    {
        var settings = new AppSettings();

        var clone = settings.Clone();
        clone.History.ConfirmDelete = false;

        Assert.True(settings.History.ConfirmDelete);
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, true, false)]
    public void ShouldSaveRecording_ShouldHonourInjectDictationSetting(
        bool enabled, bool injectText, bool saveInject, bool expected)
    {
        var history = new HistorySettings { Enabled = enabled, SaveInjectDictations = saveInject };

        Assert.Equal(expected, history.ShouldSaveRecording(injectText));
    }

    [Fact]
    public void MaxAudioStorageBytes_ShouldConvertMegabytes() =>
        Assert.Equal(2048L * 1024 * 1024, new HistorySettings().MaxAudioStorageBytes);
}
