using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class RetentionPolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private const long Mb = 1024 * 1024;

    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    private static RetentionCandidate Rec(string id, int daysAgo, long mb, bool isProtected = false) =>
        new(id, Now.AddDays(-daysAgo), mb * Mb, isProtected);

    [Fact]
    public void Plan_ShouldDeleteAudioOlderThanRetention()
    {
        var plan = RetentionPolicy.Plan([Rec("old", 31, 1), Rec("edge", 30, 1), Rec("new", 1, 1)], Now, 30, 0);

        var decision = Assert.Single(plan);
        Assert.Equal("old", decision.RecordingId);
        Assert.Equal(RetentionReason.Age, decision.Reason);
    }

    [Fact]
    public void Plan_ShouldKeepEverythingForeverWithoutCap()
    {
        var plan = RetentionPolicy.Plan([Rec("a", 3000, 500), Rec("b", 1, 500)], Now, 0, 0);

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_ShouldDeleteOldestFirstUntilUnderCap()
    {
        var plan = RetentionPolicy.Plan(
            [Rec("newest", 1, 400), Rec("oldest", 9, 400), Rec("middle", 5, 400)], Now, 0, 500 * Mb);

        Assert.Equal(["oldest", "middle"], plan.Select(d => d.RecordingId));
        Assert.All(plan, d => Assert.Equal(RetentionReason.StorageCap, d.Reason));
    }

    [Fact]
    public void Plan_ShouldApplyCapAfterAgeRule()
    {
        var plan = RetentionPolicy.Plan(
            [Rec("expired", 40, 300), Rec("b", 10, 300), Rec("c", 5, 300), Rec("d", 1, 300)], Now, 30, 700 * Mb);

        Assert.Equal(["expired", "b"], plan.Select(d => d.RecordingId));
        Assert.Equal([RetentionReason.Age, RetentionReason.StorageCap], plan.Select(d => d.Reason));
    }

    [Fact]
    public void Plan_ShouldNeverTouchProtectedRecordings()
    {
        var plan = RetentionPolicy.Plan(
            [Rec("active", 100, 900, isProtected: true), Rec("other", 2, 100)], Now, 30, 500 * Mb);

        Assert.Equal(["other"], plan.Select(d => d.RecordingId));
    }

    [Fact]
    public void Plan_ShouldIgnoreRecordingsWithoutAudio()
    {
        var plan = RetentionPolicy.Plan([Rec("textonly", 100, 0)], Now, 30, 1);

        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_ShouldUseSettings()
    {
        var settings = new HistorySettings { AudioRetentionDays = 7, MaxAudioStorageMb = 0 };

        var plan = RetentionPolicy.Plan([Rec("a", 8, 1), Rec("b", 6, 1)], Now, settings);

        Assert.Equal(["a"], plan.Select(d => d.RecordingId));
    }

    [Fact]
    public void Apply_ShouldDeleteOnlyAudioOfExpiredRecordingsInTheStore()
    {
        var store = new RecordingStore(Path.Combine(_directory.Path, "sessions"), new ManualTimeProvider(Now));
        var old = CreateWithAudio(store, Now.AddDays(-40));
        var fresh = CreateWithAudio(store, Now.AddDays(-1));
        var writing = store.CreateRecording(Now.AddDays(-50));
        File.WriteAllBytes(Path.Combine(writing.FolderPath, "audio.ogg.part"), new byte[10]);

        var applied = RetentionPolicy.Apply(store, new HistorySettings { AudioRetentionDays = 30 }, Now);

        Assert.Equal([old], applied.Select(d => d.RecordingId));
        Assert.Null(store.GetAudioPath(old));
        Assert.NotNull(store.LoadCurrentTranscript(old));
        Assert.NotNull(store.GetAudioPath(fresh));
        Assert.True(File.Exists(Path.Combine(writing.FolderPath, "audio.ogg.part")));
    }

    private static string CreateWithAudio(RecordingStore store, DateTimeOffset startedAt)
    {
        var entry = store.CreateRecording(startedAt);
        store.AddVersion(entry.Id, HistoryFixtures.LiveSpec(), HistoryFixtures.Transcript((0, 1, "hello")));
        File.WriteAllBytes(Path.Combine(entry.FolderPath, "audio.ogg"), new byte[50]);
        store.FinishRecording(entry.Id, startedAt.AddMinutes(1), new AudioInfo { File = "audio.ogg", Bytes = 50 });
        return entry.Id;
    }
}
