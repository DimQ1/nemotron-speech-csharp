using System.Security.Cryptography;
using System.Text.Json;

namespace VoiceType.Uno.History;

/// <summary>
/// Persistent queue of history jobs (<c>jobs/queue.json</c>) and its single writer.
/// Every change is written atomically. On load, jobs left <see cref="JobState.Running"/>
/// by a killed app go back to <see cref="JobState.Queued"/> so they resume (from their
/// <see cref="HistoryJob.Checkpoint"/>). Methods are thread-safe and return copies. The
/// runner observes <see cref="Changed"/> / <see cref="Get"/> to stop a job that was paused
/// or cancelled while running.
/// <para>
/// A file with a schema newer than <see cref="JobQueueDocument.CurrentSchema"/> (written by a
/// newer app) is opened read-only: it is shown but never rewritten, never run and never
/// changed, so job kinds and fields this version does not know are not lost
/// (<see cref="IsReadOnly"/>).
/// </para>
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
        IsReadOnly = _document.Schema > JobQueueDocument.CurrentSchema;
        if (resumed && !IsReadOnly)
            Save();
    }

    /// <summary>
    /// The file was written by a newer app (schema above <see cref="JobQueueDocument.CurrentSchema"/>):
    /// nothing is written, enqueued, started or changed.
    /// </summary>
    public bool IsReadOnly { get; }

    /// <summary>Schema of the loaded file (the current schema for a new queue).</summary>
    public int FileSchema
    {
        get
        {
            lock (_gate)
                return _document.Schema;
        }
    }

    /// <summary>Whole-queue pause: running jobs are stopped by the runner, nothing new starts.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _document.Paused;
        }
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
        ThrowIfReadOnly();

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

    /// <summary>Queued or running → paused (progress and checkpoint are kept).</summary>
    public bool Pause(string jobId) =>
        Transition(jobId, j => j.State is JobState.Queued or JobState.Running, j => j.State = JobState.Paused);

    /// <summary>Paused → queued; it continues from its checkpoint.</summary>
    public bool Resume(string jobId) =>
        Transition(jobId, j => j.State == JobState.Paused, j => j.State = JobState.Queued);

    /// <summary>Any unfinished state → cancelled (the checkpoint is dropped).</summary>
    public bool Cancel(string jobId) =>
        Transition(jobId, j => !j.IsFinished, j =>
        {
            j.State = JobState.Cancelled;
            j.Checkpoint = null;
            j.FinishedAt = _time.GetLocalNow();
        });

    /// <summary>Failed or cancelled → queued again, progress, checkpoint and error cleared.</summary>
    public bool Retry(string jobId) =>
        Transition(jobId, j => j.State is JobState.Failed or JobState.Cancelled, j =>
        {
            j.State = JobState.Queued;
            j.Progress = 0;
            j.Checkpoint = null;
            j.Error = null;
            j.FinishedAt = null;
        });

    /// <summary>
    /// Replaces the start condition of an unfinished job ("Edit schedule"; "Run now" passes
    /// <see cref="JobCondition.Now"/>). A paused job is queued again.
    /// </summary>
    public bool SetCondition(string jobId, JobCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return Transition(jobId, j => !j.IsFinished, j =>
        {
            j.Condition = HistoryJson.Clone(condition, HistoryJsonContext.Default.JobCondition);
            if (j.State == JobState.Paused)
                j.State = JobState.Queued;
        });
    }

    public int SetBatchCondition(string batchId, JobCondition condition) =>
        ForBatch(batchId, id => SetCondition(id, condition));

    /// <summary>Pauses or resumes the whole queue (design §5.6 <c>paused</c>).</summary>
    public bool SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (IsReadOnly || _document.Paused == paused)
                return false;
            _document.Paused = paused;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    public int PauseBatch(string batchId) => ForBatch(batchId, Pause);
    public int ResumeBatch(string batchId) => ForBatch(batchId, Resume);
    public int CancelBatch(string batchId) => ForBatch(batchId, Cancel);

    /// <summary>Moves a job to <paramref name="newIndex"/> in the FIFO order (clamped).</summary>
    public bool Move(string jobId, int newIndex)
    {
        lock (_gate)
        {
            if (IsReadOnly)
                return false;
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

    /// <summary>
    /// Puts the given jobs into the given order, in the positions they occupy now (other jobs do
    /// not move). The Queue page passes its pending cards with one card swapped with its neighbour.
    /// Unknown ids are ignored.
    /// </summary>
    public bool Reorder(IReadOnlyList<string> orderedJobIds)
    {
        ArgumentNullException.ThrowIfNull(orderedJobIds);
        lock (_gate)
        {
            if (IsReadOnly)
                return false;
            var wanted = orderedJobIds.Distinct(StringComparer.Ordinal)
                .Select(Find).OfType<HistoryJob>().ToList();
            var set = wanted.ToHashSet();
            var slots = Enumerable.Range(0, _document.Jobs.Count).Where(i => set.Contains(_document.Jobs[i])).ToList();
            if (slots.Count < 2)
                return false;
            var changed = false;
            for (var k = 0; k < slots.Count; k++)
            {
                if (!ReferenceEquals(_document.Jobs[slots[k]], wanted[k]))
                    changed = true;
            }
            if (!changed)
                return false;
            for (var k = 0; k < slots.Count; k++)
                _document.Jobs[slots[k]] = wanted[k];
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
            if (IsReadOnly)
                return 0;
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
            if (IsReadOnly || _document.Paused)
                return null;
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

    /// <summary>
    /// Persists progress (0..1) and, when given, the resume point. Ignored unless the job is
    /// still running. The runner throttles calls (each one rewrites <c>queue.json</c>).
    /// </summary>
    public bool ReportProgress(string jobId, double progress, JobCheckpoint? checkpoint = null) =>
        Transition(jobId, j => j.State == JobState.Running, j =>
        {
            j.Progress = Math.Clamp(progress, 0, 1);
            if (checkpoint is not null)
                j.Checkpoint = HistoryJson.Clone(checkpoint, HistoryJsonContext.Default.JobCheckpoint);
        });

    /// <summary>
    /// Stores the resume point of an unfinished job: also accepted after the user paused the
    /// running job, so the last checkpoint taken before it stopped is not lost.
    /// </summary>
    public bool SaveCheckpoint(string jobId, double progress, JobCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return Transition(jobId, j => !j.IsFinished, j =>
        {
            j.Progress = Math.Clamp(progress, 0, 1);
            j.Checkpoint = HistoryJson.Clone(checkpoint, HistoryJsonContext.Default.JobCheckpoint);
        });
    }

    /// <summary>
    /// Running → queued, keeping progress and checkpoint: the job's condition stopped holding
    /// (charger unplugged, window over, recording started) or the app is shutting down.
    /// </summary>
    public bool Interrupt(string jobId) =>
        Transition(jobId, j => j.State == JobState.Running, j => j.State = JobState.Queued);

    public bool Complete(string jobId, string? resultId = null) =>
        Transition(jobId, j => j.State == JobState.Running, j =>
        {
            j.State = JobState.Completed;
            j.Progress = 1;
            j.ResultId = resultId;
            j.Checkpoint = null;
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
            if (IsReadOnly)
                return false;
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
            if (doc.Schema > JobQueueDocument.CurrentSchema)
                return doc; // read-only: shown as written, never rewritten
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

    private void Save()
    {
        if (IsReadOnly)
            return;
        // A schema-1 file is upgraded on its first write: schema 2 only adds fields.
        _document.Schema = JobQueueDocument.CurrentSchema;
        HistoryJson.WriteAtomic(_file, _document, HistoryJsonContext.Default.JobQueueDocument);
    }

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
            throw new InvalidOperationException(
                $"The job queue was written by a newer VoiceType (schema {_document.Schema}); it is read-only in this version.");
    }

    private static HistoryJob Copy(HistoryJob job) => HistoryJson.Clone(job, HistoryJsonContext.Default.HistoryJob);

    private static string NewHex() => Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
}
