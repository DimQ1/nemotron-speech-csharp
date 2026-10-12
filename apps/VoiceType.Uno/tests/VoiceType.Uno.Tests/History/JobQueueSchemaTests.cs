using System.Text.Json.Nodes;
using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>Queue schema 2: read-only newer files, chain fields, checkpoints, whole-queue pause.</summary>
public class JobQueueSchemaTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);
    private readonly string _file;

    public JobQueueSchemaTests() => _file = Path.Combine(_directory.Path, "jobs", "queue.json");

    public void Dispose() => _directory.Dispose();

    private JobQueue Open() => new(_file, _clock);

    private static SchedulerContext Ctx => new(HistoryFixtures.T0);

    private const string NewerQueue = """
        {
          "schema": 3,
          "paused": false,
          "futureField": { "x": 1 },
          "jobs": [
            { "id": "j1", "kind": "identifyMusic", "recordingId": "aaaaaa", "state": "running",
              "chainId": "ch1", "parameters": { "diarization": { "preset": "offline" } },
              "condition": { "kind": "atTime", "windowStart": "01:00:00", "windowEnd": "06:00:00", "requireCharging": true } },
            { "id": "j2", "kind": "rerecognize", "recordingId": "bbbbbb", "state": "queued", "dependsOn": "j1" }
          ]
        }
        """;

    [Fact]
    public void NewQueue_ShouldWriteCurrentSchema()
    {
        Open().Enqueue(HistoryJobKind.Rerecognize, "a");

        var json = JsonNode.Parse(File.ReadAllText(_file))!;
        Assert.Equal(JobQueueDocument.CurrentSchema, json["schema"]!.GetValue<int>());
        Assert.Equal(2, JobQueueDocument.CurrentSchema);
    }

    [Fact]
    public void Schema1File_ShouldLoadAndUpgradeOnWrite()
    {
        _directory.Write("jobs/queue.json", """
            { "schema": 1, "jobs": [ { "id": "j1", "kind": "rerecognize", "recordingId": "a", "state": "queued" } ] }
            """);

        var queue = Open();
        Assert.False(queue.IsReadOnly);
        Assert.Equal(1, queue.FileSchema);
        Assert.True(queue.Pause("j1"));

        var json = JsonNode.Parse(File.ReadAllText(_file))!;
        Assert.Equal(2, json["schema"]!.GetValue<int>());
        Assert.Equal("paused", json["jobs"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public void NewerSchema_ShouldOpenReadOnlyAndNeverRewriteTheFile()
    {
        _directory.Write("jobs/queue.json", NewerQueue);
        var before = File.ReadAllText(_file);

        var queue = Open();

        Assert.True(queue.IsReadOnly);
        Assert.Equal(3, queue.FileSchema);
        // Shown as written: the running job is not turned back into queued, kinds read as unknown.
        Assert.Equal(JobState.Running, queue.Jobs[0].State);
        Assert.Equal(HistoryJobKind.Unknown, queue.Jobs[0].Kind);
        Assert.Equal("j1", queue.Jobs[1].DependsOn);

        Assert.Null(queue.TakeNext(Ctx));
        Assert.False(queue.Pause("j2"));
        Assert.False(queue.Cancel("j1"));
        Assert.False(queue.Move("j2", 0));
        Assert.False(queue.SetPaused(true));
        Assert.False(queue.SetCondition("j2", JobCondition.Now()));
        Assert.Equal(0, queue.RemoveFinished(includeFailed: true));
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(HistoryJobKind.Rerecognize, "c"));
        Assert.Throws<InvalidOperationException>(() => queue.EnqueueBatch(HistoryJobKind.Rerecognize, ["c", "d"]));

        Assert.Equal(before, File.ReadAllText(_file));
        Assert.False(File.Exists(_file + ".corrupt"));
    }

    [Fact]
    public void ChainFieldsAndCombinedConditions_ShouldRoundTrip()
    {
        _directory.Write("jobs/queue.json", """
            { "schema": 2, "jobs": [
              { "id": "j1", "kind": "rerecognize", "recordingId": "a", "state": "queued", "chainId": "ch1",
                "condition": { "kind": "atTime", "windowStart": "01:00:00", "windowEnd": "06:00:00", "requireCharging": true, "requireIdle": true } },
              { "id": "j2", "kind": "translate", "recordingId": "a", "state": "queued", "chainId": "ch1", "dependsOn": "j1" } ] }
            """);

        var queue = Open();
        queue.Move("j2", 0);
        var reloaded = Open().Jobs;

        Assert.Equal("j2", reloaded[0].Id);
        Assert.Equal("ch1", reloaded[0].ChainId);
        Assert.Equal("j1", reloaded[0].DependsOn);
        Assert.True(reloaded[1].Condition.RequireCharging);
        Assert.True(reloaded[1].Condition.RequireIdle);
    }

    [Fact]
    public void Checkpoint_ShouldSurviveRestartPauseAndInterrupt_AndClearOnRetryAndComplete()
    {
        var queue = Open();
        var job = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        queue.TakeNext(Ctx);
        Assert.True(queue.ReportProgress(job.Id, 0.4, new JobCheckpoint { Sample = 16000 * 30, PartFile = "p.json" }));

        // Killed app: running → queued, checkpoint kept.
        var restarted = Open();
        var resumed = restarted.Get(job.Id)!;
        Assert.Equal(JobState.Queued, resumed.State);
        Assert.Equal(16000 * 30, resumed.Checkpoint!.Sample);
        Assert.Equal(0.4, resumed.Progress);

        restarted.TakeNext(Ctx);
        Assert.True(restarted.Interrupt(job.Id));
        Assert.Equal(JobState.Queued, restarted.Get(job.Id)!.State);
        Assert.NotNull(restarted.Get(job.Id)!.Checkpoint);

        restarted.TakeNext(Ctx);
        Assert.True(restarted.Pause(job.Id));
        Assert.True(restarted.Resume(job.Id));
        Assert.NotNull(restarted.Get(job.Id)!.Checkpoint);

        restarted.Cancel(job.Id);
        Assert.Null(restarted.Get(job.Id)!.Checkpoint);
        restarted.Retry(job.Id);
        Assert.Equal(0, restarted.Get(job.Id)!.Progress);

        restarted.TakeNext(Ctx);
        restarted.ReportProgress(job.Id, 0.5, new JobCheckpoint { Sample = 1 });
        restarted.Complete(job.Id, "v2");
        Assert.Null(restarted.Get(job.Id)!.Checkpoint);
    }

    [Fact]
    public void Interrupt_ShouldOnlyApplyToRunningJobs()
    {
        var queue = Open();
        var job = queue.Enqueue(HistoryJobKind.Rerecognize, "a");

        Assert.False(queue.Interrupt(job.Id));
        queue.TakeNext(Ctx);
        queue.Pause(job.Id);
        Assert.False(queue.Interrupt(job.Id));
        Assert.Equal(JobState.Paused, queue.Get(job.Id)!.State);
    }

    [Fact]
    public void SaveCheckpoint_ShouldApplyToPausedButNotToFinishedJobs()
    {
        var queue = Open();
        var job = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        queue.TakeNext(Ctx);
        queue.Pause(job.Id);

        Assert.False(queue.ReportProgress(job.Id, 0.3));
        Assert.True(queue.SaveCheckpoint(job.Id, 0.3, new JobCheckpoint { Sample = 42 }));
        Assert.Equal(42, Open().Get(job.Id)!.Checkpoint!.Sample);

        queue.Cancel(job.Id);
        Assert.False(queue.SaveCheckpoint(job.Id, 0.5, new JobCheckpoint { Sample = 43 }));
    }

    [Fact]
    public void Reorder_ShouldPermuteOnlyTheGivenJobsInTheirSlots()
    {
        var queue = Open();
        var a = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        var batch = queue.EnqueueBatch(HistoryJobKind.Rerecognize, ["b1", "b2"]);
        var done = queue.Enqueue(HistoryJobKind.Rerecognize, "d");
        queue.Cancel(done.Id);
        var c = queue.Enqueue(HistoryJobKind.Rerecognize, "c");

        // Move the batch card above "a"; the cancelled job keeps its slot.
        Assert.True(queue.Reorder([batch[0].Id, batch[1].Id, a.Id, c.Id]));

        Assert.Equal([batch[0].Id, batch[1].Id, a.Id, done.Id, c.Id], Open().Jobs.Select(j => j.Id));
        Assert.False(queue.Reorder([batch[0].Id, batch[1].Id, a.Id, c.Id]));
    }

    [Fact]
    public void SetPaused_ShouldStopTakingJobsAndPersist()
    {
        var queue = Open();
        queue.Enqueue(HistoryJobKind.Rerecognize, "a");

        Assert.True(queue.SetPaused(true));
        Assert.False(queue.SetPaused(true));
        Assert.Null(queue.TakeNext(Ctx));
        Assert.True(Open().IsPaused);

        Assert.True(queue.SetPaused(false));
        Assert.NotNull(queue.TakeNext(Ctx));
    }

    [Fact]
    public void SetCondition_ShouldReplaceConditionAndRequeuePausedJobs()
    {
        var queue = Open();
        var jobs = queue.EnqueueBatch(HistoryJobKind.Rerecognize, ["a", "b"], condition: JobCondition.Window(new(1, 0), new(6, 0)));
        queue.Pause(jobs[0].Id);

        Assert.Equal(2, queue.SetBatchCondition(jobs[0].BatchId!, JobCondition.Now()));

        Assert.All(queue.Jobs, j =>
        {
            Assert.Equal(JobConditionKind.Now, j.Condition.Kind);
            Assert.Equal(JobState.Queued, j.State);
        });
    }
}
