using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceType.Uno.History;

/// <summary>
/// Persistent queue of history jobs (<c>jobs/queue.json</c>) and its single writer.
/// Every change is written atomically. On load, jobs left <see cref="JobState.Running"/>
/// by a killed app go back to <see cref="JobState.Queued"/> so they resume. Methods
/// are thread-safe and return copies. The runner observes <see cref="Changed"/> /
/// <see cref="Get"/> to stop a job that was paused or cancelled while running.
/// </summary>
public sealed class JobQueue
{
    public const string QueueFileName = "queue.json";

    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly string _file;
    private JobQueueDocument _document;

    public JobQueue(string queueFile, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueFile);
        _file = queueFile;
        _time = timeProvider ?? TimeProvider.System;
        _document = Load(out var error, out var resumed);
        LoadError = error;
        if (resumed)
            Save();
    }

    /// <summary>Set when queue.json was unreadable; it was kept as <c>queue.json.corrupt</c>.</summary>
    public string? LoadError { get; }

    public string FilePath => _file;

    /// <summary>Raised (outside the lock) after any change.</summary>
    public event Action? Changed;

    /// <summary>Snapshot in queue (FIFO) order.</summary>
    public IReadOnlyList<HistoryJob> Jobs
    {
        get
        {
            lock (_gate)
                return _document.Jobs.Select(Copy).ToList();
        }
    }

    public HistoryJob? Get(string jobId)
    {
        lock (_gate)
            return Find(jobId) is { } job ? Copy(job) : null;
    }

    /// <summary>Queued/waiting/running/paused jobs (for the menu badge).</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _document.Jobs.Count(j => !j.IsFinished);
        }
    }

    // ── Enqueue ─────────────────────────────────────────────────────────────

    public HistoryJob Enqueue(
        HistoryJobKind kind,
        string recordingId,
        JobParameters? parameters = null,
        JobCondition? condition = null,
        int priority = JobPriorities.Normal) =>
        EnqueueCore(kind, [recordingId], parameters, condition, priority, batchId: null)[0];

    /// <summary>One batch (shared batch id and parameters) over many recordings.</summary>
    public IReadOnlyList<HistoryJob> EnqueueBatch(
        HistoryJobKind kind,
        IEnumerable<string> recordingIds,
        JobParameters? parameters = null,
        JobCondition? condition = null,
        int priority = JobPriorities.Normal)
    {
        ArgumentNullException.ThrowIfNull(recordingIds);
        var ids = recordingIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            throw new ArgumentException("A batch needs at least one recording.", nameof(recordingIds));
        return EnqueueCore(kind, ids, parameters, condition, priority, batchId: "b" + NewHex());
    }

    private List<HistoryJob> EnqueueCore(
        HistoryJobKind kind, List<string> recordingIds, JobParameters? parameters,
        JobCondition? condition, int priority, string? batchId)
    {
        if (kind == HistoryJobKind.Unknown)
            throw new ArgumentException("Unknown job kind.", nameof(kind));

        var added = new List<HistoryJob>();
        lock (_gate)
        {
            var now = _time.GetLocalNow();
            foreach (var recordingId in recordingIds)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
                var job = new HistoryJob
                {
                    Id = "j" + NewHex(),
                    Kind = kind,
                    BatchId = batchId,
                    RecordingId = recordingId,
                    Parameters = parameters is null ? new JobParameters() : HistoryJson.Clone(parameters, HistoryJsonContext.Default.JobParameters),
                    Condition = condition is null ? JobCondition.Now() : HistoryJson.Clone(condition, HistoryJsonContext.Default.JobCondition),
                    Priority = priority,
                    State = JobState.Queued,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _document.Jobs.Add(job);
                added.Add(Copy(job));
            }
            Save();
        }
        Changed?.Invoke();
        return added;
    }

    // ── User control ────────────────────────────────────────────────────────

    /// <summary>Queued or running → paused.</summary>
    public bool Pause(string jobId) =>
        Transition(jobId, j => j.State is JobState.Queued or JobState.Running, j => j.State = JobState.Paused);

    /// <summary>Paused → queued (a paused running job restarts from scratch).</summary>
    public bool Resume(string jobId) =>
        Transition(jobId, j => j.State == JobState.Paused, j => j.State = JobState.Queued);

    /// <summary>Any unfinished state → cancelled.</summary>
    public bool Cancel(string jobId) =>
        Transition(jobId, j => !j.IsFinished, j =>
        {
            j.State = JobState.Cancelled;
            j.FinishedAt = _time.GetLocalNow();
        });

    /// <summary>Failed or cancelled → queued again, progress and error cleared.</summary>
    public bool Retry(string jobId) =>
        Transition(jobId, j => j.State is JobState.Failed or JobState.Cancelled, j =>
        {
            j.State = JobState.Queued;
            j.Progress = 0;
            j.Error = null;
            j.FinishedAt = null;
        });

    public int PauseBatch(string batchId) => ForBatch(batchId, Pause);
    public int ResumeBatch(string batchId) => ForBatch(batchId, Resume);
    public int CancelBatch(string batchId) => ForBatch(batchId, Cancel);

    /// <summary>Moves a job to <paramref name="newIndex"/> in the FIFO order (clamped).</summary>
    public bool Move(string jobId, int newIndex)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            if (job is null)
                return false;
            _document.Jobs.Remove(job);
            _document.Jobs.Insert(Math.Clamp(newIndex, 0, _document.Jobs.Count), job);
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drops completed and cancelled jobs (failed ones stay for retry unless <paramref name="includeFailed"/>).</summary>
    public int RemoveFinished(bool includeFailed = false)
    {
        int removed;
        lock (_gate)
        {
            removed = _document.Jobs.RemoveAll(j =>
                j.State is JobState.Completed or JobState.Cancelled || (includeFailed && j.State == JobState.Failed));
            if (removed > 0)
                Save();
        }
        if (removed > 0)
            Changed?.Invoke();
        return removed;
    }

    // ── Runner side ─────────────────────────────────────────────────────────

    /// <summary>Atomically picks the next job (<see cref="JobScheduler.SelectNext"/>) and marks it running.</summary>
    public HistoryJob? TakeNext(SchedulerContext context)
    {
        HistoryJob? taken;
        lock (_gate)
        {
            var next = JobScheduler.SelectNext(_document.Jobs, context);
            if (next is null)
                return null;
            Start(next);
            Save();
            taken = Copy(next);
        }
        Changed?.Invoke();
        return taken;
    }

    /// <summary>Marks a queued job running (attempts + 1).</summary>
    public bool MarkRunning(string jobId) =>
        Transition(jobId, j => j.State == JobState.Queued, Start);

    /// <summary>Persists progress (0..1). Ignored unless the job is still running.</summary>
    public bool ReportProgress(string jobId, double progress) =>
        Transition(jobId, j => j.State == JobState.Running, j => j.Progress = Math.Clamp(progress, 0, 1));

    public bool Complete(string jobId, string? resultId = null) =>
        Transition(jobId, j => j.State == JobState.Running, j =>
        {
            j.State = JobState.Completed;
            j.Progress = 1;
            j.ResultId = resultId;
            j.Error = null;
            j.FinishedAt = _time.GetLocalNow();
        });

    public bool Fail(string jobId, string error) =>
        Transition(jobId, j => j.State == JobState.Running, j =>
        {
            j.State = JobState.Failed;
            j.Error = error;
            j.FinishedAt = _time.GetLocalNow();
        });

    // ── Internals ───────────────────────────────────────────────────────────

    private void Start(HistoryJob job)
    {
        job.State = JobState.Running;
        job.Attempts++;
        job.Error = null;
        job.StartedAt = _time.GetLocalNow();
    }

    private bool Transition(string jobId, Func<HistoryJob, bool> allowed, Action<HistoryJob> apply)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            if (job is null || !allowed(job))
                return false;
            apply(job);
            job.UpdatedAt = _time.GetLocalNow();
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    private int ForBatch(string batchId, Func<string, bool> action)
    {
        List<string> ids;
        lock (_gate)
            ids = _document.Jobs.Where(j => j.BatchId == batchId).Select(j => j.Id).ToList();
        return ids.Count(action);
    }

    private HistoryJob? Find(string jobId) => _document.Jobs.Find(j => j.Id == jobId);

    private JobQueueDocument Load(out string? error, out bool resumed)
    {
        error = null;
        resumed = false;
        if (!File.Exists(_file))
            return new JobQueueDocument();
        try
        {
            var doc = HistoryJson.Read(_file, HistoryJsonContext.Default.JobQueueDocument);
            doc.Jobs.RemoveAll(j => j is null);
            foreach (var job in doc.Jobs.Where(j => j.State == JobState.Running))
            {
                job.State = JobState.Queued;
                resumed = true;
            }
            return doc;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            try { File.Copy(_file, _file + ".corrupt", overwrite: true); } catch (IOException) { }
            return new JobQueueDocument();
        }
    }

    private void Save() => HistoryJson.WriteAtomic(_file, _document, HistoryJsonContext.Default.JobQueueDocument);

    private static HistoryJob Copy(HistoryJob job) => HistoryJson.Clone(job, HistoryJsonContext.Default.HistoryJob);

    private static string NewHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
}
