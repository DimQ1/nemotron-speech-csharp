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

    /// <summary>One step of a chain: a job kind and its parameters.</summary>
    public sealed record ChainStep(HistoryJobKind Kind, JobParameters Parameters);

    /// <summary>
    /// Queues one Reprocess chain (design 11.4): ordinary jobs in order, each <see cref="HistoryJob.DependsOn"/> the previous
    /// one, sharing a <see cref="HistoryJob.ChainId"/>, the condition and the priority. Returns the jobs in step order.
    /// </summary>
    public IReadOnlyList<HistoryJob> EnqueueChain(
        string recordingId, IReadOnlyList<ChainStep> steps, JobCondition? condition = null,
        int priority = JobPriorities.Normal, string? batchId = null) =>
        EnqueueChains([(recordingId, steps)], condition, priority, batchId).SelectMany(c => c).ToList();

    /// <summary>
    /// Several chains in one write, one per recording, inserted one after another so the FIFO order takes chain A to its
    /// end before chain B starts (A2 comes before B1).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<HistoryJob>> EnqueueChains(
        IReadOnlyList<(string RecordingId, IReadOnlyList<ChainStep> Steps)> chains,
        JobCondition? condition = null, int priority = JobPriorities.Normal, string? batchId = null)
    {
        ArgumentNullException.ThrowIfNull(chains);
        ThrowIfReadOnly();
        var result = new List<IReadOnlyList<HistoryJob>>();
        lock (_gate)
        {
            var now = _time.GetLocalNow();
            foreach (var (recordingId, steps) in chains)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(recordingId);
                if (steps.Count == 0 || steps.Any(st => st.Kind == HistoryJobKind.Unknown))
                    throw new ArgumentException("A chain needs at least one known step.", nameof(chains));
                var chainId = "ch" + NewHex();
                var added = new List<HistoryJob>();
                HistoryJob? previous = null;
                foreach (var step in steps)
                {
                    var job = new HistoryJob
                    {
                        Id = "j" + NewHex(),
                        Kind = step.Kind,
                        BatchId = batchId,
                        ChainId = chainId,
                        DependsOn = previous?.Id,
                        RecordingId = recordingId,
                        Parameters = HistoryJson.Clone(step.Parameters, HistoryJsonContext.Default.JobParameters),
                        Priority = priority,
                        Condition = condition is null ? JobCondition.Now() : HistoryJson.Clone(condition, HistoryJsonContext.Default.JobCondition),
                        State = JobState.Queued,
                        CreatedAt = now,
                        UpdatedAt = now,
                    };
                    _document.Jobs.Add(job);
                    added.Add(Copy(job));
                    previous = job;
                }
                result.Add(added);
            }
            Save();
        }
        Changed?.Invoke();
        return result;
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
            CancelDependants(j.Id);
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
            RestoreDependants(j.Id);
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

    public int PauseChain(string chainId) => ForChain(chainId, Pause);
    public int ResumeChain(string chainId) => ForChain(chainId, Resume);

    /// <summary>
    /// Cancels the unfinished steps of a chain, last step first, so none of them is mistaken for a casualty of a failed
    /// predecessor (that would let a later Retry of an earlier step bring it back).
    /// </summary>
    public int CancelChain(string chainId) => ForChain(chainId, Cancel, reverse: true);

    /// <summary>Replaces the start condition of every unfinished step of a chain ("Edit schedule", "Run now").</summary>
    public int SetChainCondition(string chainId, JobCondition condition) =>
        ForChain(chainId, id => SetCondition(id, condition));

    /// <summary>The jobs of one chain in step order.</summary>
    public IReadOnlyList<HistoryJob> ChainJobs(string chainId)
    {
        lock (_gate)
            return _document.Jobs.Where(j => j.ChainId == chainId).Select(Copy).ToList();
    }

    /// <summary>
    /// Records that the "make current at the end of the chain" of a re-recognition step was handled, so it is not repeated
    /// after the user chose another version.
    /// </summary>
    public bool MarkChainApplied(string jobId) =>
        Transition(jobId, j => j.ChainApplied != true, j => j.ChainApplied = true);

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
            // A finished step of a chain that is still running stays (the end of the chain needs the re-recognition's result),
            // and so does a re-recognition whose "make current at the end" has not been carried out yet.
            var pendingChains = _document.Jobs.Where(j => j.ChainId is not null && !j.IsFinished).Select(j => j.ChainId!).ToHashSet();
            removed = _document.Jobs.RemoveAll(j =>
                (j.State is JobState.Completed or JobState.Cancelled || (includeFailed && j.State == JobState.Failed))
                && !(j.ChainId is not null && pendingChains.Contains(j.ChainId))
                && !(j.Kind == HistoryJobKind.Rerecognize && j.State == JobState.Completed
                     && j.Parameters.MakeCurrentAtChainEnd == true && j.ChainApplied != true));
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
            HandOverVersion(j);
        });

    public bool Fail(string jobId, string error) =>
        Transition(jobId, j => j.State == JobState.Running, j =>
        {
            j.State = JobState.Failed;
            j.Error = error;
            j.FinishedAt = _time.GetLocalNow();
            CancelDependants(j.Id);
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

    /// <summary>Error text of steps cancelled because an earlier step failed or was cancelled.</summary>
    public const string PreviousStepFailed = "previous step failed";

    /// <summary>
    /// A step that failed or was cancelled takes the unfinished steps that depend on it (transitively) down with it; they get
    /// <see cref="PreviousStepFailed"/> so <see cref="Retry"/> of the failed step can bring them back. Under the lock.
    /// </summary>
    private void CancelDependants(string jobId)
    {
        foreach (var dependant in _document.Jobs.Where(d => d.DependsOn == jobId && !d.IsFinished).ToList())
        {
            dependant.State = JobState.Cancelled;
            dependant.Checkpoint = null;
            dependant.Error = PreviousStepFailed;
            dependant.FinishedAt = _time.GetLocalNow();
            dependant.UpdatedAt = dependant.FinishedAt.Value;
            CancelDependants(dependant.Id);
        }
    }

    /// <summary>Retry of a step restores the steps that were cancelled because of it (and only those).</summary>
    private void RestoreDependants(string jobId)
    {
        foreach (var dependant in _document.Jobs.Where(d => d.DependsOn == jobId
                     && d.State == JobState.Cancelled && d.Error == PreviousStepFailed).ToList())
        {
            dependant.State = JobState.Queued;
            dependant.Progress = 0;
            dependant.Checkpoint = null;
            dependant.Error = null;
            dependant.FinishedAt = null;
            dependant.UpdatedAt = _time.GetLocalNow();
            RestoreDependants(dependant.Id);
        }
    }

    /// <summary>
    /// A finished step tells the next one which version it works on: the version a re-recognition created, or the one the
    /// step itself worked on (so Diarize and Analyze of a chain always handle the chain's version, not the current one).
    /// </summary>
    private void HandOverVersion(HistoryJob completed)
    {
        var version = completed.Kind == HistoryJobKind.Rerecognize ? completed.ResultId : completed.Parameters.SourceVersion;
        if (version is null)
            return;
        foreach (var dependant in _document.Jobs.Where(d => d.DependsOn == completed.Id && d.Parameters.SourceVersion is null))
            dependant.Parameters.SourceVersion = version;
    }

    private int ForChain(string chainId, Func<string, bool> action, bool reverse = false)
    {
        List<string> ids;
        lock (_gate)
            ids = _document.Jobs.Where(j => j.ChainId == chainId).Select(j => j.Id).ToList();
        if (reverse)
            ids.Reverse();
        return ids.Count(action);
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
