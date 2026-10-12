using VoiceType.Uno.History;
using VoiceType.Uno.Services.Jobs;
using Xunit;

namespace VoiceType.Uno.Tests.History;

/// <summary>A Reprocess chain through the real <see cref="JobRunner"/> with scripted executors.</summary>
public class JobRunnerChainTests : IAsyncDisposable
{
    private readonly SpeakerFixtures _f = new();
    private readonly JobQueue _queue;
    private readonly ReprocessChain _chain;
    private readonly List<string> _order = [];
    private readonly Dictionary<HistoryJobKind, Func<HistoryJob, string?>> _scripts = [];
    private JobRunner? _runner;

    public JobRunnerChainTests()
    {
        _queue = new JobQueue(Path.Combine(_f.Directory.Path, "jobs", "queue.json"));
        _chain = new ReprocessChain(_queue, _f.Store);
        _chain.Attach();
    }

    public async ValueTask DisposeAsync()
    {
        if (_runner is not null)
            await _runner.DisposeAsync();
        _f.Dispose();
    }

    private sealed class Device : IDeviceConditions
    {
        public bool HasBattery => false;
        public bool IsCharging => true;
        public event Action? Changed { add { } remove { } }
        public void Dispose() { }
    }

    private sealed class Scripted(HistoryJobKind kind, Func<HistoryJob, string?> run) : IJobExecutor
    {
        public HistoryJobKind Kind => kind;
        public Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken)
        {
            context.ReportProgress(0.5);
            return Task.FromResult(run(job));
        }
    }

    private JobRunner StartRunner()
    {
        IJobExecutor[] executors =
        [
            new Scripted(HistoryJobKind.Rerecognize, job =>
            {
                _order.Add("rerecognize");
                return _scripts.TryGetValue(job.Kind, out var s) ? s(job) : _f.Store.AddVersion(job.RecordingId,
                    HistoryFixtures.LiveSpec(), _f.Store.LoadTranscript(job.RecordingId, "v1"), makeCurrent: job.Parameters.MakeCurrent == true).Id;
            }),
            new Scripted(HistoryJobKind.Diarize, job =>
            {
                _order.Add("diarize:" + job.Parameters.SourceVersion);
                return _scripts.TryGetValue(job.Kind, out var s) ? s(job) : "s1";
            }),
            new Scripted(HistoryJobKind.Analyze, job =>
            {
                _order.Add("analyze:" + job.Parameters.SourceVersion);
                return _scripts.TryGetValue(job.Kind, out var s) ? s(job) : "a1";
            }),
        ];
        _runner = new JobRunner(_queue, executors, new Device(), new RecordingActivity(), () => new HistorySettings(),
            options: new JobRunnerOptions { PollInterval = TimeSpan.FromMilliseconds(100), SaveInterval = TimeSpan.FromMilliseconds(10) });
        _runner.Start();
        return _runner;
    }

    private async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
            await Task.Delay(20);
        Assert.True(done(), "timed out; queue: " + string.Join(", ", _queue.Jobs.Select(j => j.Kind + ":" + j.State)));
    }

    [Fact]
    public async Task Chain_ShouldRunStepsInOrderHandOverTheVersionAndSwitchAtTheEnd()
    {
        var id = _f.CreateRecording();
        _chain.Enqueue([id], new ReprocessOptions { ModelFolder = "m" });

        StartRunner();
        await WaitAsync(() => _queue.Jobs.All(j => j.State == JobState.Completed));

        Assert.Equal(["rerecognize", "diarize:v2", "analyze:v2"], _order);
        Assert.Equal("v2", _f.Store.Get(id)!.Manifest.CurrentVersion);
        Assert.Equal(["v2", "s1", "a1"], _queue.Jobs.Select(j => j.ResultId));
    }

    [Fact]
    public async Task Chain_ShouldStopAtAFailedStepKeepNewTextAndResumeAfterRetry()
    {
        var id = _f.CreateRecording();
        var attempts = 0;
        _scripts[HistoryJobKind.Diarize] = _ => attempts++ == 0 ? throw new InvalidOperationException("model not installed") : "s1";
        var jobs = _chain.Enqueue([id], new ReprocessOptions { ModelFolder = "m" });

        StartRunner();
        await WaitAsync(() => _queue.Get(jobs[1].Id)!.State == JobState.Failed);

        Assert.Equal(JobState.Cancelled, _queue.Get(jobs[2].Id)!.State);
        Assert.Equal("model not installed", _queue.Get(jobs[1].Id)!.Error);
        // The new text is kept and made current even though speakers failed (design 11.2).
        await WaitAsync(() => _f.Store.Get(id)!.Manifest.CurrentVersion == "v2");

        _queue.Retry(jobs[1].Id);
        _runner!.Poke();
        await WaitAsync(() => _queue.Jobs.All(j => j.State == JobState.Completed));

        Assert.Equal(["rerecognize", "diarize:v2", "diarize:v2", "analyze:v2"], _order);
    }

    [Fact]
    public async Task Chains_OfSeveralRecordings_ShouldRunOneAfterAnother()
    {
        var a = _f.CreateRecording();
        var b = _f.CreateRecording();
        _chain.Enqueue([a, b], new ReprocessOptions { ModelFolder = "m", Analyze = false });

        StartRunner();
        await WaitAsync(() => _queue.Jobs.All(j => j.State == JobState.Completed));

        Assert.Equal(["rerecognize", "diarize:v2", "rerecognize", "diarize:v2"], _order);
        Assert.Equal("v2", _f.Store.Get(a)!.Manifest.CurrentVersion);
        Assert.Equal("v2", _f.Store.Get(b)!.Manifest.CurrentVersion);
    }
}
