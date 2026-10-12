using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class HistoryUiRowsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    private static RecordingManifest Manifest(string id, DateTimeOffset startedAt, bool audio = true) => new()
    {
        Id = id,
        StartedAt = startedAt,
        Audio = audio ? new AudioInfo { File = "audio.ogg", DurationSec = 60, Bytes = 1000 } : null,
        CurrentVersion = "v1",
        Versions = [new TranscriptVersion { Id = "v1", Excerpt = "Okay let's start the meeting about the budget" }],
    };

    [Fact]
    public void GroupOf_ShouldSplitTodayYesterdayEarlier()
    {
        Assert.Equal(HistoryDateGroup.Today, HistoryUiRows.GroupOf(Now.AddHours(-17), Now));
        Assert.Equal(HistoryDateGroup.Yesterday, HistoryUiRows.GroupOf(Now.AddHours(-19), Now));
        Assert.Equal(HistoryDateGroup.Earlier, HistoryUiRows.GroupOf(Now.AddDays(-2), Now));
    }

    [Fact]
    public void Badges_ShouldListTranslationChatsVersionInjectAndNoAudio()
    {
        var m = Manifest("a", Now, audio: false);
        m.Mode.InjectText = true;
        m.Versions.Add(new TranscriptVersion { Id = "v2" });
        m.CurrentVersion = "v2";
        m.Translations.Add(new TranslationInfo { Id = "t1", TargetLanguage = "ru", SourceVersion = "v1" });
        m.Translations.Add(new TranslationInfo { Id = "t2", TargetLanguage = "ru", SourceVersion = "v2" });
        m.Translations.Add(new TranslationInfo { Id = "t3", TargetLanguage = "de", Status = TranslationStatuses.Failed });

        var badges = HistoryUiRows.Badges(m, chatCount: 2);

        Assert.Equal(["→ RU", "2 chats", "v2", "Inject", "No audio"], badges.Select(b => b.Text));
        Assert.Equal(HistoryBadgeKind.Accent, badges[0].Kind);
        Assert.Equal(HistoryBadgeKind.Warning, badges[^1].Kind);
        Assert.Equal("Current version v2 of 2", badges[2].AutomationName);
    }

    [Fact]
    public void Badges_ShouldBeEmptyForAPlainRecording() =>
        Assert.Empty(HistoryUiRows.Badges(Manifest("a", Now), chatCount: 0));

    [Fact]
    public void Build_ShouldUseEffectiveTitleAndDuration()
    {
        var row = HistoryUiRows.Build(Manifest("a", Now.AddHours(-1)), 1, Now);

        Assert.Equal("Okay let's start the meeting about the budget", row.Title);
        Assert.Equal("1:00", row.DurationText);
        Assert.Equal(["1 chat"], row.Badges.Select(b => b.Text));
        Assert.Contains("1 chat", row.AutomationName);
        Assert.Equal(1000, row.AudioBytes);
    }

    [Fact]
    public void Group_ShouldKeepOrderInsideGroupsAndSkipEmptyOnes()
    {
        var rows = new[]
        {
            HistoryUiRows.Build(Manifest("a", Now.AddHours(-1)), 0, Now),
            HistoryUiRows.Build(Manifest("b", Now.AddHours(-2)), 0, Now),
            HistoryUiRows.Build(Manifest("c", Now.AddDays(-5)), 0, Now),
        };

        var groups = HistoryUiRows.Group(rows, Now);

        Assert.Equal(["TODAY", "EARLIER"], groups.Select(g => g.Label));
        Assert.Equal(["a", "b"], groups[0].Rows.Select(r => r.Id));
        Assert.Single(HistoryUiRows.Group(rows, Now, byDate: false));
        Assert.Empty(HistoryUiRows.Group([], Now));
    }

    [Fact]
    public void Snippet_ShouldShowTheMatchingSegmentWithItsTime()
    {
        var doc = HistorySearch.CreateDocument(Manifest("a", Now),
            HistoryFixtures.Transcript((0, 5, "Hello there"), (200, 210, "Budget-wise we are about five percent over in cloud costs")));
        var hit = HistorySearch.Search([doc], "cloud").Single();

        Assert.Equal("03:20  Budget-wise we are about five percent over in cloud costs", HistoryUiRows.Snippet(hit));
        Assert.Null(HistoryUiRows.Snippet(HistorySearch.Search([doc], "").Single()));
    }

    [Fact]
    public void SelectionSubtitle_ShouldSumAudio()
    {
        var rows = new[] { HistoryUiRows.Build(Manifest("a", Now), 0, Now, audioBytes: 21 * 1024 * 1024), HistoryUiRows.Build(Manifest("b", Now), 0, Now, audioBytes: 20 * 1024 * 1024) };
        Assert.Equal("41 MB of audio", HistoryUiRows.SelectionSubtitle(rows));
        Assert.Equal("No audio", HistoryUiRows.SelectionSubtitle([]));
    }
}
