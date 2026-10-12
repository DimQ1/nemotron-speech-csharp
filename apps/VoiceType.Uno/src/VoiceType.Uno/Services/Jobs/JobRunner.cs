using VoiceType.Uno.History;

namespace VoiceType.Uno.Services.Jobs;

public sealed class JobRunnerOptions
{
    /// <summary>Re-evaluation period for time conditions (windows, not-before) and settings.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>queue.json is rewritten with progress/checkpoints at most this often (design §5.6).</summary>
    public TimeSpan SaveInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>In-memory progress events for the UI at most this often.</summary>
    public TimeSpan PublishInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The single worker of the history job queue (design §9.5): picks the next job with
/// <see cref="JobQueue.TakeNext"/> under the current <see cref="SchedulerContext"/>, runs it on a
/// background thread — one heavy job at a time — and reports progress and checkpoints.
/// <list type="bullet">
/// <item>Pause / Cancel (Queue page or notification) stop the running job at the next batch;
/// a paused job keeps its checkpoint, a cancelled one loses its partial files.</item>
/// <item>When the job's condition stops holding (charger unplugged, window over, a recording
/// started with <c>When idle</c> or with jobs disallowed during recording), the whole queue is
/// paused, or the app shuts down, the job is interrupted: its checkpoint is saved and it goes
/// back to queued, so it resumes later — also after a killed app (the queue file turns
/// running jobs back into queued on load).</item>
/// <item>Re-evaluates on queue changes, power and recording changes, and every
/// <see cref="JobRunnerOptions.PollInterval"/> for time windows.</item>
/// </list>
/// </summary>
public sealed class JobRunner : IAsyncDisposable
{
    private enum StopReason { None, User, Interrupted }

    private readonly JobQueue _queue;
    private readonly IReadOnlyDictionary<HistoryJobKind, IJobExecutor> _executors;
    private readonly IDeviceConditions _device;
    private readonly IRecordingActivity _recording;
    private readonly Func<HistorySettings> _historySettings;
    private readonly IJobsKeepAlive _keepAlive;
    private readonly TimeProvider _time;
    private readonly JobRunnerOptions _options;
    private readonly string _jobsDir;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();

    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private Timer? _poll;

    // Running job (guarded by _gate).
    private HistoryJob? _runningJob;
    private CancellationTokenSource? _jobCancel;
    private StopReason _stopReason;
    private JobExecutionContext? _context;
    private JobRunStatus? _status;
    private long _lastPublishTicks;

    public JobRunner(
        JobQueue queue,
        IEnumerable<IJobExecutor> executors,
        IDeviceConditions device,
        IRecordingActivity recording,
        Func<HistorySettings> historySettings,
        IJobsKeepAlive? keepAlive = null,
        TimeProvider? time = null,
        JobRunnerOptions? options = null)
    {
        _queue = queue;
        _executors = executors.ToDictionary(e => e.Kind);
        _device = device;
        _recording = recording;
        _historySettings = historySettings;
        _keepAlive = keepAlive ?? new NullJobsKeepAlive();
        _time = time ?? TimeProvider.System;
        _options = options ?? new JobRunnerOptions();
        _jobsDir = Path.GetDirectoryName(Path.GetFullPath(queue.FilePath))!;
    }

    public JobQueue Queue => _queue;

    /// <summary>Raised (any thread) when the running job or its progress changes.</summary>
    public event Action? StatusChanged;

    public bool IsStarted => _loop is not null;

    /// <summary>Progress of the running job, or null when none runs.</summary>
    public JobRunStatus? Status
    {
        get
        {
            lock (_gate)
                return _status;
        }
    }

    /// <summary>Device and app state the scheduler decides on right now.</summary>
    public SchedulerContext CurrentContext() => new(
        _time.GetLocalNow(),
        IsLiveRecording: _recording.IsLiveRecording,
        IsCharging: _device.IsCharging,
        // "Battery-powered platform where WhenCharging matters": phones and laptops.
        IsAndroid: OperatingSystem.IsAndroid() || _device.HasBattery,
        AllowDuringRecording: _historySettings().AllowJobsDuringRecording);

    /// <summary>Why <paramref name="job"/> is not running now (for the Queue page).</summary>
    public JobWaitReason GetWaitReason(HistoryJob job, IReadOnlyList<HistoryJob> queue) =>
        JobScheduler.GetWaitReason(job, queue, CurrentContext());

    /// <summary>Starts the worker. Safe to call more than once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _stopping = new CancellationTokenSource();
            CleanupOrphanPartFiles();
            _queue.Changed += OnQueueChanged;
            _device.Changed += OnConditionsChanged;
            _recording.Changed += OnConditionsChanged;
            _keepAlive.PauseRequested += OnNotificationPause;
            _keepAlive.CancelRequested += OnNotificationCancel;
            _keepAlive.TimedOut += OnKeepAliveTimedOut;
            _poll = new Timer(_ => OnConditionsChanged(), null, _options.PollInterval, _options.PollInterval);
            var token = _stopping.Token;
            _loop = Task.Factory.StartNew(() => LoopAsync(token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>Asks the worker to look at the queue now (after an enqueue, a setting change…).</summary>
    public void Poke() => Wake();

    /// <summary>
    /// Stops the worker. A running job is interrupted with its checkpoint saved, so it continues
    /// on the next start.
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
            if (loop is null)
                return;
            _loop = null;
            _queue.Changed -= OnQueueChanged;
            _device.Changed -= OnConditionsChanged;
            _recording.Changed -= OnConditionsChanged;
            _keepAlive.PauseRequested -= OnNotificationPause;
            _keepAlive.CancelRequested -= OnNotificationCancel;
            _keepAlive.TimedOut -= OnKeepAliveTimedOut;
            _poll?.Dispose();
            _poll = null;
        }
        RequestStop(StopReason.Interrupted);
        _stopping?.Cancel();
        Wake();
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping?.Dispose();
        _wake.Dispose();
    }

    // ── Worker ──────────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            HistoryJob? job = null;
            try
            {
                job = _queue.TakeNext(CurrentContext());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Job queue unavailable: {ex.Message}");
            }

            if (job is null)
            {
                try
                {
                    await _wake.WaitAsync(_options.PollInterval, stopping).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            await RunJobAsync(job, stopping).ConfigureAwait(false);
            DrainWake();
        }
    }

    private async Task RunJobAsync(HistoryJob job, CancellationToken stopping)
    {
        if (!_executors.TryGetValue(job.Kind, out var executor))
        {
            _queue.Fail(job.Id, $"This version cannot run '{job.Kind}' jobs.");
            return;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var context = new JobExecutionContext(job, _jobsDir, _queue, _time, _options.SaveInterval, OnProgress);
        lock (_gate)
        {
            _runningJob = job;
            _jobCancel = cancel;
            _stopReason = StopReason.None;
            _context = context;
            _status = context.Snapshot();
        }
        StatusChanged?.Invoke();
        _keepAlive.Start(Notice(job, _status));

        // The job may have been paused/cancelled or its condition lost between TakeNext and here.
        CheckRunningJob();

        try
        {
            var resultId = await Task.Factory.StartNew(
                    () => executor.RunAsync(job, context, cancel.Token),
                    cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .Unwrap().ConfigureAwait(false);
            if (!_queue.Complete(job.Id, resultId))
                Console.WriteLine($"[VoiceType.Uno] Job {job.Id} finished but was no longer running (result {resultId}).");
            DeletePartFile(job.Id);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            StopReason reason;
            lock (_gate)
                reason = _stopReason;
            if (reason == StopReason.None)
                reason = StopReason.Interrupted; // app shutdown

            var state = _queue.Get(job.Id)?.State;
            if (state is JobState.Running or JobState.Paused)
                context.Flush(); // keep the last resume point
            if (reason == StopReason.Interrupted)
                _queue.Interrupt(job.Id);
            if (_queue.Get(job.Id) is null or { State: JobState.Cancelled })
                DeletePartFile(job.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Job {job.Id} failed: {ex}");
            _queue.Fail(job.Id, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _runningJob = null;
                _jobCancel = null;
                _context = null;
                _status = null;
            }
            _keepAlive.Stop();
            StatusChanged?.Invoke();
            // A recognizer just went away: give its native/managed buffers back before the next job.
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // ── Stop conditions ─────────────────────────────────────────────────────

    private void OnQueueChanged()
    {
        CheckRunningJob();
        Wake();
    }

    private void OnConditionsChanged()
    {
        CheckRunningJob();
        Wake();
    }

    /// <summary>Stops the running job when the user paused/cancelled it or its condition no longer holds.</summary>
    private void CheckRunningJob()
    {
        HistoryJob? running;
        lock (_gate)
            running = _runningJob;
        if (running is null)
            return;

        var current = _queue.Get(running.Id);
        if (current is null || current.State != JobState.Running)
        {
            RequestStop(StopReason.User);
            return;
        }
        if (_queue.IsPaused)
        {
            RequestStop(StopReason.Interrupted);
            return;
        }

        var context = CurrentContext();
        var lost = (context.IsLiveRecording && !context.AllowDuringRecording)
            || JobScheduler.GetConditionReason(current.Condition, context) != JobWaitReason.None;
        if (lost)
            RequestStop(StopReason.Interrupted);
    }

    private void RequestStop(StopReason reason)
    {
        lock (_gate)
        {
            if (_jobCancel is null || _jobCancel.IsCancellationRequested)
                return;
            _stopReason = reason;
            try { _jobCancel.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void OnNotificationPause()
    {
        if (Status?.JobId is { } id)
            _queue.Pause(id);
    }

    private void OnNotificationCancel()
    {
        if (Status?.JobId is { } id)
            _queue.Cancel(id);
    }

    private void OnKeepAliveTimedOut() => RequestStop(StopReason.Interrupted);

    // ── Progress ────────────────────────────────────────────────────────────

    private void OnProgress(JobRunStatus status)
    {
        HistoryJob? job;
        lock (_gate)
        {
            if (_runningJob?.Id != status.JobId)
                return;
            _status = status;
            job = _runningJob;
            if (_time.GetElapsedTime(_lastPublishTicks) < _options.PublishInterval && status.Fraction < 1)
                return;
            _lastPublishTicks = _time.GetTimestamp();
        }
        _keepAlive.Update(Notice(job, status));
        StatusChanged?.Invoke();
    }

    private static JobNotice Notice(HistoryJob job, JobRunStatus? status)
    {
        var title = job.Kind == HistoryJobKind.Rerecognize ? "Re-recognizing a recording" : JobDisplay.KindTitle(job.Kind);
        if (status is null)
            return new JobNotice(title, "Starting…", null);
        var percent = (int)Math.Floor(status.Fraction * 100);
        var text = status.Detail ?? $"{percent}% · {JobDisplay.FormatEta(status.Remaining)}";
        return new JobNotice(title, text, percent);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void DrainWake()
    {
        while (_wake.CurrentCount > 0 && _wake.Wait(0))
        {
        }
    }

    private void DeletePartFile(string jobId) => OfflinePartFile.Delete(OfflinePartFile.PathFor(_jobsDir, jobId));

    /// <summary>Partial results of jobs that are gone or finished (e.g. cancelled while the app was closed).</summary>
    private void CleanupOrphanPartFiles()
    {
        try
        {
            // A newer app's queue (read-only here) may own part files this version does not know.
            if (_queue.IsReadOnly || !Directory.Exists(_jobsDir))
                return;
            var unfinished = _queue.Jobs.Where(j => !j.IsFinished).Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(_jobsDir, "*" + OfflinePartFile.Suffix))
            {
                var id = Path.GetFileName(file)[..^OfflinePartFile.Suffix.Length];
                if (!unfinished.Contains(id))
                    OfflinePartFile.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
