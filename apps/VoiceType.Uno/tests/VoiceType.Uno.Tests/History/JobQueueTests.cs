using VoiceType.Uno.History;
using VoiceType.Uno.Tests.Fakes;
using Xunit;

namespace VoiceType.Uno.Tests.History;

public class JobQueueTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly ManualTimeProvider _clock = new(HistoryFixtures.T0);
    private readonly string _file;

    public JobQueueTests() => _file = Path.Combine(_directory.Path, "jobs", "queue.json");

    public void Dispose() => _directory.Dispose();

    private JobQueue Open() => new(_file, _clock);

    private static SchedulerContext Ctx => new(HistoryFixtures.T0);

    [Fact]
    public void Enqueue_ShouldPersistAndCopyParameters()
    {
        var parameters = new JobParameters { ModelFolder = "parakeet", Language = "ru" };
        var job = Open().Enqueue(HistoryJobKind.Rerecognize, "aaaaaa", parameters, JobCondition.WhenIdle(), JobPriorities.High);
        parameters.Language = "en";

        var loaded = Assert.Single(Open().Jobs);
        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal("ru", loaded.Parameters.Language);
        Assert.Equal(JobConditionKind.WhenIdle, loaded.Condition.Kind);
        Assert.Equal(JobPriorities.High, loaded.Priority);
        Assert.Equal(JobState.Queued, loaded.State);
        Assert.Equal(HistoryFixtures.T0, loaded.CreatedAt);
    }

    [Fact]
    public void EnqueueBatch_ShouldShareBatchIdAndParameters()
    {
        var queue = Open();

        var jobs = queue.EnqueueBatch(HistoryJobKind.Translate, ["a", "b", "a", "c"],
            new JobParameters { TargetLanguage = "de" });

        Assert.Equal(3, jobs.Count);
        Assert.Single(jobs.Select(j => j.BatchId).Distinct());
        Assert.NotNull(jobs[0].BatchId);
        Assert.All(jobs, j => Assert.Equal("de", j.Parameters.TargetLanguage));
        Assert.Equal(3, queue.PendingCount);
        Assert.Throws<ArgumentException>(() => queue.EnqueueBatch(HistoryJobKind.Translate, []));
    }

    [Fact]
    public void TakeNext_ShouldMarkRunningAndBlockFurtherHeavyJobs()
    {
        var queue = Open();
        var first = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        queue.Enqueue(HistoryJobKind.AutoTitle, "b");

        var taken = queue.TakeNext(Ctx);

        Assert.Equal(first.Id, taken!.Id);
        Assert.Equal(JobState.Running, taken.State);
        Assert.Equal(1, taken.Attempts);
        Assert.Null(queue.TakeNext(Ctx));
    }

    [Fact]
    public void ProgressAndComplete_ShouldPersistAndFreeTheSlot()
    {
        var queue = Open();
        var a = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        var b = queue.Enqueue(HistoryJobKind.Rerecognize, "b");
        queue.TakeNext(Ctx);

        Assert.True(queue.ReportProgress(a.Id, 0.5));
        Assert.Equal(0.5, Open().Get(a.Id)!.Progress);
        Assert.True(queue.Complete(a.Id, "v2"));

        var done = queue.Get(a.Id)!;
        Assert.Equal(JobState.Completed, done.State);
        Assert.Equal("v2", done.ResultId);
        Assert.Equal(1, done.Progress);
        Assert.Equal(b.Id, queue.TakeNext(Ctx)!.Id);
    }

    [Fact]
    public void Load_ShouldTurnRunningJobsBackIntoQueuedAfterRestart()
    {
        var queue = Open();
        var job = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        queue.TakeNext(Ctx);
        queue.ReportProgress(job.Id, 0.42);

        var restarted = Open();

        var resumed = restarted.Get(job.Id)!;
        Assert.Equal(JobState.Queued, resumed.State);
        Assert.Equal(0.42, resumed.Progress);
        Assert.Equal(JobState.Queued, new JobQueue(_file).Get(job.Id)!.State);
        Assert.Equal(job.Id, restarted.TakeNext(Ctx)!.Id);
        Assert.Equal(2, restarted.Get(job.Id)!.Attempts);
    }

    [Fact]
    public void PauseResumeCancelRetry_ShouldFollowStateRules()
    {
        var queue = Open();
        var job = queue.Enqueue(HistoryJobKind.Rerecognize, "a");

        Assert.True(queue.Pause(job.Id));
        Assert.Null(queue.TakeNext(Ctx));
        Assert.False(queue.Pause(job.Id));
        Assert.True(queue.Resume(job.Id));
        queue.TakeNext(Ctx);
        Assert.True(queue.Cancel(job.Id));
        Assert.False(queue.ReportProgress(job.Id, 0.9));
        Assert.False(queue.Complete(job.Id));
        Assert.Equal(JobState.Cancelled, queue.Get(job.Id)!.State);
        Assert.True(queue.Retry(job.Id));
        Assert.Equal(JobState.Queued, queue.Get(job.Id)!.State);
        Assert.Equal(0, queue.Get(job.Id)!.Progress);
    }

    [Fact]
    public void Fail_ShouldKeepErrorUntilRetry()
    {
        var queue = Open();
        var job = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        queue.TakeNext(Ctx);

        Assert.True(queue.Fail(job.Id, "model missing"));
        Assert.Equal("model missing", Open().Get(job.Id)!.Error);
        Assert.True(queue.Retry(job.Id));
        Assert.Null(queue.Get(job.Id)!.Error);
    }

    [Fact]
    public void BatchOperations_ShouldApplyToEveryJobOfTheBatch()
    {
        var queue = Open();
        var batch = queue.EnqueueBatch(HistoryJobKind.Rerecognize, ["a", "b", "c"]);
        var batchId = batch[0].BatchId!;

        Assert.Equal(3, queue.PauseBatch(batchId));
        Assert.Null(queue.TakeNext(Ctx));
        Assert.Equal(3, queue.ResumeBatch(batchId));
        Assert.Equal(3, queue.CancelBatch(batchId));
        Assert.Equal(3, queue.RemoveFinished());
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public void Move_ShouldReorderFifo()
    {
        var queue = Open();
        var a = queue.Enqueue(HistoryJobKind.Rerecognize, "a");
        var b = queue.Enqueue(HistoryJobKind.Rerecognize, "b");
        var c = queue.Enqueue(HistoryJobKind.Rerecognize, "c");

        Assert.True(queue.Move(c.Id, 0));
        Assert.Equal([c.Id, a.Id, b.Id], Open().Jobs.Select(j => j.Id));
        Assert.True(queue.Move(c.Id, 99));
        Assert.Equal([a.Id, b.Id, c.Id], queue.Jobs.Select(j => j.Id));
        Assert.Equal(a.Id, queue.TakeNext(Ctx)!.Id);
    }

    [Fact]
    public void Load_ShouldStartEmptyAndKeepCopyWhenFileIsCorrupted()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ broken");

        var queue = Open();

        Assert.NotNull(queue.LoadError);
        Assert.Empty(queue.Jobs);
        Assert.True(File.Exists(_file + ".corrupt"));
    }

    [Fact]
    public void Jobs_ShouldReturnCopies()
    {
        var queue = Open();
        queue.Enqueue(HistoryJobKind.Rerecognize, "a");

        queue.Jobs[0].State = JobState.Failed;

        Assert.Equal(JobState.Queued, queue.Jobs[0].State);
    }

    [Fact]
    public void Enqueue_ShouldKeepEveryConcurrentJob()
    {
        var queue = Open();

        Parallel.For(0, 30, i => queue.Enqueue(HistoryJobKind.Summarize, "r" + i));

        Assert.Equal(30, Open().Jobs.Count);
    }
}
