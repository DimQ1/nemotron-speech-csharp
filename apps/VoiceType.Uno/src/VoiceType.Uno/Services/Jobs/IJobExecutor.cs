using VoiceType.Uno.History;

namespace VoiceType.Uno.Services.Jobs;

/// <summary>
/// Runs one kind of history job (re-recognize now; translate / summarize / autoTitle later).
/// Called by <see cref="JobRunner"/> on a background thread, one heavy job at a time.
/// Throws <see cref="OperationCanceledException"/> when the cancellation token
/// fires; any other exception fails the job with its message.
/// </summary>
public interface IJobExecutor
{
    HistoryJobKind Kind { get; }

    /// <returns>Id of what the job produced (e.g. the new version <c>v3</c>).</returns>
    Task<string?> RunAsync(HistoryJob job, JobExecutionContext context, CancellationToken cancellationToken);
}

/// <summary>Live progress of the running job (in memory; the queue keeps the persisted fraction).</summary>
/// <param name="Detail">Executor's own line, e.g. "Loading the model…".</param>
public sealed record JobRunStatus(
    string JobId,
    double Fraction,
    TimeSpan? ProcessedAudio,
    TimeSpan? TotalAudio,
    TimeSpan? Remaining,
    string? Detail);

/// <summary>
/// What an executor reports to the runner: progress (persisted at most every
/// <see cref="JobRunnerOptions.SaveInterval"/>) and resume points (written together with the
/// progress, and flushed when the job is interrupted or paused).
/// </summary>
public sealed class JobExecutionContext
{
    private readonly JobQueue _queue;
    private readonly TimeProvider _time;
    private readonly TimeSpan _saveInterval;
    private readonly Action<JobRunStatus> _publish;
    private readonly Lock _gate = new();
    private readonly double _startFraction;
    private readonly long _startTicks;
    private long _lastSaveTicks;
    private double _fraction;
    private Func<JobCheckpoint>? _pendingCheckpoint;
    private TimeSpan? _processed, _total;
    private string? _detail;

    internal JobExecutionContext(HistoryJob job, string jobsDir, JobQueue queue, TimeProvider time, TimeSpan saveInterval, Action<JobRunStatus> publish)
    {
        Job = job;
        JobsDir = jobsDir;
        _queue = queue;
        _time = time;
        _saveInterval = saveInterval;
        _publish = publish;
        _startFraction = _fraction = job.Progress;
        _startTicks = _lastSaveTicks = time.GetTimestamp();
    }

    public HistoryJob Job { get; }

    /// <summary>Folder of <c>queue.json</c>; executors keep partial results here.</summary>
    public string JobsDir { get; }

    /// <summary>A short status line for the Queue page (model loading, decoding audio…).</summary>
    public void ReportDetail(string? detail)
    {
        lock (_gate)
            _detail = detail;
        Publish();
    }

    /// <summary>Progress 0..1 with the audio position when known.</summary>
    public void ReportProgress(double fraction, TimeSpan? processedAudio = null, TimeSpan? totalAudio = null)
    {
        lock (_gate)
        {
            _fraction = Math.Clamp(fraction, 0, 1);
            _processed = processedAudio;
            _total = totalAudio;
        }
        Publish();
        SaveIfDue(force: false);
    }

    /// <summary>
    /// Offers a resume point. <paramref name="persist"/> writes the executor's partial result and
    /// returns the checkpoint for the queue; it runs at most once per save interval, and once
    /// more when the job stops early (<see cref="Flush"/>).
    /// </summary>
    public void OfferCheckpoint(Func<JobCheckpoint> persist)
    {
        lock (_gate)
            _pendingCheckpoint = persist;
        SaveIfDue(force: false);
    }

    /// <summary>Writes a pending checkpoint and the latest progress now (the job is stopping early).</summary>
    internal void Flush() => SaveIfDue(force: true);

    internal JobRunStatus Snapshot()
    {
        lock (_gate)
        {
            var elapsed = _time.GetElapsedTime(_startTicks);
            var remaining = JobDisplay.EstimateRemaining(elapsed, _fraction - _startFraction, 1 - _fraction);
            return new JobRunStatus(Job.Id, _fraction, _processed, _total, remaining, _detail);
        }
    }

    private void Publish() => _publish(Snapshot());

    private void SaveIfDue(bool force)
    {
        Func<JobCheckpoint>? persist;
        double fraction;
        lock (_gate)
        {
            if (!force && _time.GetElapsedTime(_lastSaveTicks) < _saveInterval)
                return;
            _lastSaveTicks = _time.GetTimestamp();
            persist = _pendingCheckpoint;
            _pendingCheckpoint = null;
            fraction = _fraction;
        }

        if (persist is not null)
            _queue.SaveCheckpoint(Job.Id, fraction, persist());
        else
            _queue.ReportProgress(Job.Id, fraction);
    }
}
