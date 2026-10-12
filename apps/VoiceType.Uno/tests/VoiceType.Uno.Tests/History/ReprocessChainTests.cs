using VoiceType.Uno.History;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>Reprocess chains as ordinary queue jobs: dependsOn, failure/cancel/retry, make-current at the end (design §11).</summary>
public class ReprocessChainTests : IDisposable
{
    private readonly SpeakerFixtures _f = new();
    private readonly JobQueue _queue;
    private readonly ReprocessChain _chain;

    public ReprocessChainTests()
    {
        _queue = new JobQueue(Path.Combine(_f.Directory.Path, "jobs", "queue.json"), _f.Clock);
        _chain = new ReprocessChain(_queue, _f.Store);
    }

    public void Dispose() => _f.Dispose();

    private static SchedulerContext Ctx => new(HistoryFixtures.T0);

    private IReadOnlyList<HistoryJob> Enqueue(string id, ReprocessOptions? options = null) =>
        _chain.Enqueue([id], options ?? new ReprocessOptions { ModelFolder = "parakeet-fp32" });

    /// <summary>Runs the job the scheduler picks and completes it with <paramref name="result"/>.</summary>
    private HistoryJob Step(string? result)
    {
        var job = _queue.TakeNext(Ctx) ?? throw new InvalidOperationException("nothing to run");
        Assert.True(_queue.Complete(job.Id, result));
        return job;
    }

    [Fact]
    public void Enqueue_ShouldLinkThreeStepsByChainIdAndDependsOn()
    {
        var id = _f.CreateRecording();

        var jobs = Enqueue(id);

        Assert.Equal([HistoryJobKind.Rerecognize, HistoryJobKind.Diarize, HistoryJobKind.Analyze], jobs.Select(j => j.Kind));
        Assert.Single(jobs.Select(j => j.ChainId).Distinct());
        Assert.NotNull(jobs[0].ChainId);
        Assert.Null(jobs[0].DependsOn);
        Assert.Equal(jobs[0].Id, jobs[1].DependsOn);
        Assert.Equal(jobs[1].Id, jobs[2].DependsOn);
        Assert.Equal("parakeet-fp32", jobs[0].Parameters.ModelFolder);
        Assert.False(jobs[0].Parameters.MakeCurrent == true);
        Assert.True(jobs[0].Parameters.MakeCurrentAtChainEnd);
        Assert.Equal(["rerecognize", "diarize", "analyze"], jobs[0].Parameters.ChainSteps);
        Assert.Equal(ReprocessOrigins.User, jobs[1].Parameters.Origin);
    }

    [Fact]
    public void Enqueue_ShouldRunStepsStrictlyInOrderOneAtATime()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);

        Assert.Equal(JobWaitReason.WaitingForPreviousStep, JobScheduler.GetWaitReason(_queue.Get(jobs[1].Id)!, _queue.Jobs, Ctx));
        var first = _queue.TakeNext(Ctx)!;
        Assert.Equal(jobs[0].Id, first.Id);
        Assert.Null(_queue.TakeNext(Ctx)); // the rest waits (heavy job running, and for the previous step)
        _queue.Complete(first.Id, "v2");
        Assert.Equal(jobs[1].Id, _queue.TakeNext(Ctx)!.Id);
    }

    [Fact]
    public void Complete_ShouldHandTheNewVersionToTheNextSteps()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);

        Step("v2");
        var diarize = _queue.Get(jobs[1].Id)!;
        Assert.Equal("v2", diarize.Parameters.SourceVersion);

        Step("s1");
        Assert.Equal("v2", _queue.Get(jobs[2].Id)!.Parameters.SourceVersion);
    }

    [Fact]
    public void Enqueue_WithoutRecognition_ShouldPinTheCurrentVersion()
    {
        var id = _f.CreateRecording();

        var jobs = Enqueue(id, new ReprocessOptions { Rerecognize = false });

        Assert.Equal([HistoryJobKind.Diarize, HistoryJobKind.Analyze], jobs.Select(j => j.Kind));
        Assert.All(jobs, j => Assert.Equal("v1", j.Parameters.SourceVersion));
    }

    [Fact]
    public void Enqueue_RecognitionAlone_ShouldMakeTheVersionCurrentItself()
    {
        var id = _f.CreateRecording();

        var job = Assert.Single(Enqueue(id, new ReprocessOptions { Diarize = false, Analyze = false }));

        Assert.True(job.Parameters.MakeCurrent);
        Assert.Null(job.Parameters.MakeCurrentAtChainEnd);
    }

    [Fact]
    public void Enqueue_ShouldShareTheConditionAndPriorityAcrossTheChain()
    {
        var id = _f.CreateRecording();
        var condition = JobCondition.Window(new TimeOnly(1, 0), new TimeOnly(6, 0));
        condition.RequireCharging = true;

        var jobs = Enqueue(id, new ReprocessOptions { Condition = condition, Priority = JobPriorities.High });

        Assert.All(jobs, j =>
        {
            Assert.Equal(JobConditionKind.AtTime, j.Condition.Kind);
            Assert.True(j.Condition.RequireCharging);
            Assert.Equal(new TimeOnly(1, 0), j.Condition.WindowStart);
            Assert.Equal(JobPriorities.High, j.Priority);
        });
        Assert.NotSame(jobs[0].Condition, jobs[1].Condition);

        _queue.SetChainCondition(jobs[0].ChainId!, JobCondition.Now());
        Assert.All(_queue.ChainJobs(jobs[0].ChainId!), j => Assert.Equal(JobConditionKind.Now, j.Condition.Kind));
    }

    [Fact]
    public void Enqueue_ManyRecordings_ShouldFormOneBatchOfChainsRunOneAfterAnother()
    {
        var a = _f.CreateRecording();
        var b = _f.CreateRecording();

        var jobs = _chain.Enqueue([a, b], new ReprocessOptions { ModelFolder = "m" });

        Assert.Equal(6, jobs.Count);
        Assert.Single(jobs.Select(j => j.BatchId).Distinct());
        Assert.NotNull(jobs[0].BatchId);
        Assert.Equal(2, jobs.Select(j => j.ChainId).Distinct().Count());
        // FIFO: A1 A2 A3 B1 B2 B3.
        Assert.Equal([a, a, a, b, b, b], jobs.Select(j => j.RecordingId));
        Assert.Equal(jobs[0].Id, _queue.TakeNext(Ctx)!.Id);
    }

    [Fact]
    public void Fail_ShouldCancelTheStepsAfterItAndRetryShouldBringThemBack()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);
        Step("v2");
        var diarize = _queue.TakeNext(Ctx)!;

        _queue.Fail(diarize.Id, "model missing");

        Assert.Equal(JobState.Failed, _queue.Get(jobs[1].Id)!.State);
        var analyze = _queue.Get(jobs[2].Id)!;
        Assert.Equal(JobState.Cancelled, analyze.State);
        Assert.Equal(JobQueue.PreviousStepFailed, analyze.Error);

        _queue.Retry(diarize.Id);

        Assert.Equal(JobState.Queued, _queue.Get(jobs[1].Id)!.State);
        analyze = _queue.Get(jobs[2].Id)!;
        Assert.Equal(JobState.Queued, analyze.State);
        Assert.Null(analyze.Error);
        Assert.Equal(jobs[1].Id, _queue.TakeNext(Ctx)!.Id);
    }

    [Fact]
    public void Cancel_ShouldCancelDependantsTransitively()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);

        _queue.Cancel(jobs[0].Id);

        Assert.All(jobs.Skip(1), j => Assert.Equal(JobState.Cancelled, _queue.Get(j.Id)!.State));
        Assert.Null(_queue.TakeNext(Ctx));
    }

    [Fact]
    public void CancelChain_ShouldNotLetARetryResurrectWhatTheUserCancelled()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);

        Assert.Equal(3, _queue.CancelChain(jobs[0].ChainId!));
        _queue.Retry(jobs[0].Id);

        Assert.Equal(JobState.Queued, _queue.Get(jobs[0].Id)!.State);
        Assert.Equal(JobState.Cancelled, _queue.Get(jobs[1].Id)!.State);
        Assert.Equal(JobState.Cancelled, _queue.Get(jobs[2].Id)!.State);
    }

    [Fact]
    public void PauseAndResumeChain_ShouldAffectEveryUnfinishedStep()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);

        Assert.Equal(3, _queue.PauseChain(jobs[0].ChainId!));
        Assert.All(_queue.ChainJobs(jobs[0].ChainId!), j => Assert.Equal(JobState.Paused, j.State));
        Assert.Null(_queue.TakeNext(Ctx));
        Assert.Equal(3, _queue.ResumeChain(jobs[0].ChainId!));
        Assert.Equal(jobs[0].Id, _queue.TakeNext(Ctx)!.Id);
    }

    [Fact]
    public void Interrupt_ShouldKeepTheChainWaitingNotCancel()
    {
        var id = _f.CreateRecording();
        var jobs = Enqueue(id);
        var first = _queue.TakeNext(Ctx)!;

        _queue.Interrupt(first.Id); // charger unplugged / window over

        Assert.Equal(JobState.Queued, _queue.Get(jobs[0].Id)!.State);
        Assert.Equal(JobState.Queued, _queue.Get(jobs[1].Id)!.State);
    }

    // ── End of chain ────────────────────────────────────────────────────

    [Fact]
    public void ApplyFinishedChains_ShouldSwitchToTheNewVersionOnlyWhenTheChainIsOver()
    {
        var id = _f.CreateRecording();
        var v2 = _f.Store.AddVersion(id, HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(id, "v1"), makeCurrent: false);
        _chain.Attach();
        Enqueue(id);

        Step(v2.Id);
        Assert.Equal("v1", _f.Store.Get(id)!.Manifest.CurrentVersion);
        Step("s1");
        Assert.Equal("v1", _f.Store.Get(id)!.Manifest.CurrentVersion);
        Step("a1");

        Assert.Equal("v2", _f.Store.Get(id)!.Manifest.CurrentVersion);
        // Handled once: a later choice of the user stays.
        _f.Store.SetCurrentVersion(id, "v1");
        _chain.ApplyFinishedChains();
        Assert.Equal("v1", _f.Store.Get(id)!.Manifest.CurrentVersion);
    }

    [Fact]
    public void ApplyFinishedChains_ShouldStillSwitchWhenALaterStepFailed()
    {
        var id = _f.CreateRecording();
        var v2 = _f.Store.AddVersion(id, HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(id, "v1"), makeCurrent: false);
        _chain.Attach();
        Enqueue(id);
        Step(v2.Id);
        var diarize = _queue.TakeNext(Ctx)!;

        _queue.Fail(diarize.Id, "no model");

        Assert.Equal("v2", _f.Store.Get(id)!.Manifest.CurrentVersion);
    }

    [Fact]
    public void ApplyFinishedChains_ShouldLeaveTheCurrentVersionWhenTheUserCancelledTheChain()
    {
        var id = _f.CreateRecording();
        var v2 = _f.Store.AddVersion(id, HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(id, "v1"), makeCurrent: false);
        _chain.Attach();
        var jobs = Enqueue(id);
        Step(v2.Id);

        _queue.CancelChain(jobs[0].ChainId!);

        Assert.Equal("v1", _f.Store.Get(id)!.Manifest.CurrentVersion);
    }

    [Fact]
    public void RemoveFinished_ShouldKeepStepsOfAChainThatIsStillRunning()
    {
        var id = _f.CreateRecording();
        var v2 = _f.Store.AddVersion(id, HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(id, "v1"), makeCurrent: false);
        _chain.Attach();
        var jobs = Enqueue(id);
        Step(v2.Id);

        Assert.Equal(0, _queue.RemoveFinished()); // the re-recognition is needed when the chain ends
        Step("s1");
        Step("a1");

        Assert.Equal("v2", _f.Store.Get(id)!.Manifest.CurrentVersion);
        Assert.Equal(3, _queue.RemoveFinished());
        Assert.Equal(jobs.Count, 3);
    }

    [Fact]
    public void Enqueue_ShouldRejectAnEmptyPlan()
    {
        var id = _f.CreateRecording();

        Assert.Throws<ArgumentException>(() =>
            _chain.Enqueue([id], new ReprocessOptions { Rerecognize = false, Diarize = false, Analyze = false }));
        Assert.Throws<ArgumentException>(() => _chain.Enqueue([], new ReprocessOptions()));
    }

    [Fact]
    public void EnqueueAuto_ShouldSkipRecognitionOfTheSameModelAndShortRecordings()
    {
        var id = _f.CreateRecording(segments: 10); // 40 s
        var settings = new ReprocessSettings
        {
            Profile = { AsrModelFolder = "parakeet-int4" }, // the live version's model folder
        };

        var jobs = _chain.EnqueueAuto(id, settings);

        Assert.Equal([HistoryJobKind.Diarize, HistoryJobKind.Analyze], jobs.Select(j => j.Kind));
        Assert.All(jobs, j =>
        {
            Assert.Equal(ReprocessOrigins.Auto, j.Parameters.Origin);
            Assert.Equal(JobPriorities.Low, j.Priority);
            Assert.True(j.Condition.RequireCharging);
        });

        var shortOne = _f.CreateRecording(segments: 2); // 8 s < 20 s
        Assert.Empty(_chain.EnqueueAuto(shortOne, settings));
    }

    [Fact]
    public void AutoNew_ShouldReplaceThePlainAfterStopJob()
    {
        var id = _f.CreateRecording(segments: 10);
        var speakers = new FakeSpeakers();
        var settings = new HistorySettings();
        settings.Reprocess.AutoNew = true;
        settings.Reprocess.Profile.AsrModelFolder = "parakeet-int4";

        var queued = SpeakerAutomation.TryEnqueueAfterSave(speakers, _chain, _f.Store, id, settings, isPhone: false);

        Assert.True(queued);
        Assert.Equal(0, speakers.EnqueueCalls); // the chain does the diarization
        Assert.Contains(_queue.Jobs, j => j.Kind == HistoryJobKind.Diarize && j.ChainId is not null);
    }

    internal sealed class FakeSpeakers : ISpeakerService
    {
        public int EnqueueCalls { get; private set; }
        public bool IsModelAvailable { get; set; } = true;
        public string ModelStatus => "fake";
        public SpeakerLayerView? GetLayer(string recordingId, string versionId) => null;
        public string? EnqueueDiarize(string recordingId, string versionId, bool runNow)
        {
            EnqueueCalls++;
            return "j1";
        }
        public void RenamePerson(string recordingId, string personId, string name) { }
        public void SetPersonColor(string recordingId, string personId, int color) { }
        public void MergePeople(string recordingId, string intoPersonId, string fromPersonId) { }
        public void SplitPerson(string recordingId, string personId) { }
        public event Action<string>? Changed { add { } remove { } }
    }
}
