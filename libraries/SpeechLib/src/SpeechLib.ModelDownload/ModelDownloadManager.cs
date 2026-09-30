using System.Diagnostics;

namespace SpeechLib.ModelDownload;

/// <summary>What to download: a whole repo (optionally one subfolder of it) or a single file.</summary>
/// <param name="Key">Identity used to de-duplicate: enqueueing a key that is already queued or running returns that job.</param>
/// <param name="Title">Name shown to the user ("Parakeet TDT 0.6B v3 · INT4").</param>
/// <param name="RepoId">Hugging Face repo id.</param>
/// <param name="TargetDirectory">Local folder the files land in.</param>
public sealed record ModelDownloadRequest(string Key, string Title, string RepoId, string TargetDirectory)
{
    /// <summary>Repo subfolder to download (Parakeet fp32/int8/int4); its prefix is stripped locally.</summary>
    public string? Subfolder { get; init; }

    /// <summary>Download only this repo file (e.g. the <c>.litertlm</c> translation model).</summary>
    public string? SingleFile { get; init; }

    /// <summary>What the job is for, so listeners can react (select an ASR model, load a translator).</summary>
    public ModelDownloadKind Kind { get; init; } = ModelDownloadKind.Recognition;

    /// <summary>Runs after all files are in place (e.g. writing a streaming profile); failures fail the job.</summary>
    public Action<string>? AfterDownload { get; init; }

    /// <summary>Catalog entry the request came from, when any.</summary>
    public ModelDescriptor? Descriptor { get; init; }

    /// <summary>A request for a catalog model under <paramref name="modelsRoot"/>.</summary>
    public static ModelDownloadRequest ForCatalogModel(ModelDescriptor model, string modelsRoot) =>
        new(model.SubfolderName, $"{model.CommercialName} · {model.Variant}", model.RepoId, Path.Combine(modelsRoot, model.SubfolderName))
        {
            Subfolder = model.QuantizationFolder,
            Descriptor = model,
        };
}

public enum ModelDownloadKind { Recognition, Translation }

public enum DownloadJobState { Queued, Downloading, Completed, Failed, Cancelled }

/// <summary>
/// One download and its live progress. Updated from worker threads; read it on
/// any thread (values are snapshots, the manager raises events on change).
/// </summary>
public sealed class DownloadJob
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _downloadedBytes;

    internal DownloadJob(ModelDownloadRequest request) => Request = request;

    public Guid Id { get; } = Guid.NewGuid();
    public ModelDownloadRequest Request { get; }
    public string Key => Request.Key;
    public string Title => Request.Title;

    public DownloadJobState State { get; internal set; } = DownloadJobState.Queued;
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);
    public long TotalBytes { get; internal set; }
    public int FilesDone { get; internal set; }
    public int FilesTotal { get; internal set; }
    public string CurrentFile { get; internal set; } = "";
    public string? Error { get; internal set; }
    public double BytesPerSecond { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }

    /// <summary>Folder of the finished model (the request's target directory).</summary>
    public string? ResultPath { get; internal set; }

    /// <summary>0–100; exact once the total is known.</summary>
    public double Percent => State == DownloadJobState.Completed
        ? 100
        : TotalBytes > 0 ? Math.Min(100, DownloadedBytes * 100.0 / TotalBytes) : 0;

    public bool IsActive => State is DownloadJobState.Queued or DownloadJobState.Downloading;

    /// <summary>Completes with the model folder, or faults / cancels with the job.</summary>
    public Task<string> Completion => _completion.Task;

    internal CancellationToken Token => _cts.Token;
    internal void AddBytes(long count) => Interlocked.Add(ref _downloadedBytes, count);
    internal void ResetBytes(long value) => Interlocked.Exchange(ref _downloadedBytes, value);
    internal void RequestCancel() => _cts.Cancel();
    internal void Finish(string path) => _completion.TrySetResult(path);
    internal void Fail(Exception ex) => _completion.TrySetException(ex);
    internal void Cancelled() => _completion.TrySetCanceled();
}

/// <summary>Totals across the jobs currently listed by the manager.</summary>
public readonly record struct DownloadTotals(
    int Active, int Completed, int Failed, long DownloadedBytes, long TotalBytes, double BytesPerSecond)
{
    public double Percent => TotalBytes > 0 ? Math.Min(100, DownloadedBytes * 100.0 / TotalBytes) : 0;
}

/// <summary>
/// Runs model downloads in parallel (bounded), tracks byte progress per job and in
/// total, and reports every job's completion. One instance per application: jobs
/// keep running when the window that started them closes.
/// </summary>
public sealed class ModelDownloadManager : IDisposable
{
    private readonly HuggingFaceClient _client;
    private readonly SemaphoreSlim _slots;
    private readonly object _gate = new();
    private readonly List<DownloadJob> _jobs = new();
    private readonly TimeSpan _progressInterval;
    private bool _disposed;

    /// <param name="maxConcurrentJobs">Jobs downloading at the same time; the rest wait in the queue.</param>
    public ModelDownloadManager(HuggingFaceClient client, int maxConcurrentJobs = 3, TimeSpan? progressInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _slots = new SemaphoreSlim(Math.Max(1, maxConcurrentJobs));
        _progressInterval = progressInterval ?? TimeSpan.FromMilliseconds(150);
    }

    /// <summary>A job was added to the list.</summary>
    public event Action<DownloadJob>? JobAdded;

    /// <summary>A job's state or progress changed (throttled while downloading).</summary>
    public event Action<DownloadJob>? JobUpdated;

    /// <summary>A job completed, failed or was cancelled.</summary>
    public event Action<DownloadJob>? JobFinished;

    /// <summary>A job was removed from the list (see <see cref="ClearFinished"/>).</summary>
    public event Action<DownloadJob>? JobRemoved;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    public bool HasActiveJobs
    {
        get { lock (_gate) return _jobs.Any(j => j.IsActive); }
    }

    public DownloadTotals Totals
    {
        get
        {
            lock (_gate)
            {
                // Totals cover the listed jobs that are running or done, so the overall
                // bar moves forward as models finish instead of resetting.
                var counted = _jobs.Where(j => j.State is not DownloadJobState.Failed and not DownloadJobState.Cancelled).ToList();
                return new DownloadTotals(
                    _jobs.Count(j => j.IsActive),
                    _jobs.Count(j => j.State == DownloadJobState.Completed),
                    _jobs.Count(j => j.State == DownloadJobState.Failed),
                    counted.Sum(j => j.State == DownloadJobState.Completed ? Math.Max(j.TotalBytes, j.DownloadedBytes) : j.DownloadedBytes),
                    counted.Sum(j => Math.Max(j.TotalBytes, j.DownloadedBytes)),
                    _jobs.Where(j => j.State == DownloadJobState.Downloading).Sum(j => j.BytesPerSecond));
            }
        }
    }

    /// <summary>The queued or running job for <paramref name="key"/>, if any.</summary>
    public DownloadJob? FindActive(string key)
    {
        lock (_gate)
            return _jobs.FirstOrDefault(j => j.IsActive && string.Equals(j.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Queues a download (or returns the job already queued/running for the same key).</summary>
    public DownloadJob Enqueue(ModelDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        DownloadJob job;
        lock (_gate)
        {
            var existing = _jobs.FirstOrDefault(j => j.IsActive && string.Equals(j.Key, request.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing;

            // A finished job for the same key is replaced by the new attempt.
            var stale = _jobs.Where(j => !j.IsActive && string.Equals(j.Key, request.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var old in stale)
                _jobs.Remove(old);
            foreach (var old in stale)
                Raise(JobRemoved, old);

            job = new DownloadJob(request);
            _jobs.Add(job);
        }

        Raise(JobAdded, job);
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public void Cancel(DownloadJob job) => job.RequestCancel();

    public void CancelAll()
    {
        foreach (var job in Jobs.Where(j => j.IsActive))
            job.RequestCancel();
    }

    /// <summary>Starts a failed or cancelled job again (the partial file is resumed).</summary>
    public DownloadJob Retry(DownloadJob job) => Enqueue(job.Request);

    /// <summary>Removes completed, failed and cancelled jobs from the list.</summary>
    public void ClearFinished()
    {
        List<DownloadJob> removed;
        lock (_gate)
        {
            removed = _jobs.Where(j => !j.IsActive).ToList();
            foreach (var job in removed)
                _jobs.Remove(job);
        }
        foreach (var job in removed)
            Raise(JobRemoved, job);
    }

    /// <summary>
    /// Drops one job from the list. A running job is cancelled first, so the
    /// partial file stays on disk and a later retry resumes it.
    /// </summary>
    public void Remove(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        job.RequestCancel();

        lock (_gate)
        {
            if (!_jobs.Remove(job))
                return;
        }

        Raise(JobRemoved, job);
    }

    private async Task RunAsync(DownloadJob job)
    {
        try
        {
            await _slots.WaitAsync(job.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Complete(job, DownloadJobState.Cancelled, null);
            return;
        }

        try
        {
            job.State = DownloadJobState.Downloading;
            job.CurrentFile = "Listing files…";
            Raise(JobUpdated, job);

            var request = job.Request;
            var files = await _client.ListFilesAsync(request.RepoId, request.Subfolder, request.SingleFile, job.Token).ConfigureAwait(false);
            if (files.Count == 0)
                throw new InvalidOperationException($"No files found in {request.RepoId}{(request.Subfolder is null ? "" : "/" + request.Subfolder)}.");

            job.FilesTotal = files.Count;
            job.TotalBytes = files.Sum(f => f.SizeBytes);

            // Files already on disk with the right size count as done (re-download after a
            // partial run only fetches what is missing).
            long done = 0;
            var pending = new List<RemoteFile>();
            foreach (var file in files)
            {
                if (HuggingFaceClient.IsComplete(file, request.TargetDirectory))
                {
                    done += file.SizeBytes;
                    job.FilesDone++;
                }
                else
                {
                    pending.Add(file);
                }
            }
            job.ResetBytes(done);
            Raise(JobUpdated, job);

            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var speedWindowStart = TimeSpan.Zero;
            var speedWindowBytes = job.DownloadedBytes;

            // Largest files first: the long transfer starts early, small files fill in.
            foreach (var file in pending.OrderByDescending(f => f.SizeBytes))
            {
                job.CurrentFile = file.LocalPath;
                var before = job.DownloadedBytes;
                try
                {
                    await _client.DownloadFileAsync(request.RepoId, file, request.TargetDirectory, count =>
                    {
                        job.AddBytes(count);
                        var now = clock.Elapsed;
                        if (now - lastReport < _progressInterval)
                            return;

                        var window = (now - speedWindowStart).TotalSeconds;
                        if (window >= 1)
                        {
                            job.BytesPerSecond = (job.DownloadedBytes - speedWindowBytes) / window;
                            speedWindowStart = now;
                            speedWindowBytes = job.DownloadedBytes;
                        }

                        lastReport = now;
                        Raise(JobUpdated, job);
                    }, job.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Keep the byte count honest for the partial file (it is resumed on retry).
                    job.ResetBytes(before);
                    throw;
                }

                // Sizes reported by the Hub are authoritative; correct any drift from resume.
                if (file.SizeBytes > 0)
                    job.ResetBytes(before + file.SizeBytes);
                job.FilesDone++;
                Raise(JobUpdated, job);
            }

            if (job.TotalBytes == 0)
                job.TotalBytes = job.DownloadedBytes;

            request.AfterDownload?.Invoke(request.TargetDirectory);
            Complete(job, DownloadJobState.Completed, null);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            Complete(job, DownloadJobState.Cancelled, null);
        }
        catch (Exception ex)
        {
            Complete(job, DownloadJobState.Failed, ex);
        }
        finally
        {
            _slots.Release();
        }
    }

    private void Complete(DownloadJob job, DownloadJobState state, Exception? error)
    {
        job.State = state;
        job.BytesPerSecond = 0;
        job.CurrentFile = "";
        job.FinishedAt = DateTimeOffset.Now;
        job.Error = error is null ? null : string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message;
        if (state == DownloadJobState.Completed)
            job.ResultPath = job.Request.TargetDirectory;

        Raise(JobUpdated, job);
        Raise(JobFinished, job);

        switch (state)
        {
            case DownloadJobState.Completed: job.Finish(job.Request.TargetDirectory); break;
            case DownloadJobState.Failed: job.Fail(error!); break;
            default: job.Cancelled(); break;
        }
    }

    /// <summary>Raised when a subscriber of a job event throws; the job itself is unaffected.</summary>
    public event Action<Exception>? SubscriberFailed;

    /// <summary>
    /// Invokes every subscriber separately. A failing subscriber (for example a UI
    /// handler touching a bound collection from this worker thread) must never turn a
    /// finished download into a failed one or stop the other subscribers.
    /// </summary>
    private void Raise(Action<DownloadJob>? handlers, DownloadJob job)
    {
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<DownloadJob>>())
        {
            try
            {
                handler(job);
            }
            catch (Exception ex)
            {
                try { SubscriberFailed?.Invoke(ex); } catch { }
                Debug.WriteLine($"[downloads] subscriber failed for {job.Title}: {ex}");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelAll();
    }
}
