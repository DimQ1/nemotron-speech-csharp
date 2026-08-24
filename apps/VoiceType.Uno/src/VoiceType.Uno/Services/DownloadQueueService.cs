using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceType.Uno.Services;

/// <summary>
/// Queue of model downloads that runs several downloads in parallel and
/// reports a single aggregated progress for the whole queue, plus per-item
/// progress. Replaces the single-download-at-a-time ModelDownloadService usage
/// for UI scenarios (the service itself stays for one-off calls).
/// </summary>
public sealed class DownloadQueueService : IDisposable
{
    private const int MaxParallelDownloads = 2;

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _parallelismGate = new(MaxParallelDownloads, MaxParallelDownloads);
    private readonly ConcurrentDictionary<Guid, DownloadQueueItem> _items = new();
    private readonly object _gate = new();

    public DownloadQueueService()
    {
        // Accept-Encoding: gzip, deflate, br; the handler transparently decompresses.
        // This keeps the HF metadata and any text payloads small over the wire.
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                | DecompressionMethods.Deflate
                | DecompressionMethods.Brotli
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VoiceType.Uno", "1.0"));
    }

    /// <summary>Snapshot of every queued/active/finished download.</summary>
    public IReadOnlyList<DownloadQueueItem> Items =>
        _items.Values.OrderBy(i => i.EnqueuedAtUtc).ToList();

    /// <summary>True while at least one item is queued or running.</summary>
    public bool IsActive => Items.Any(i => i.State is DownloadQueueItemState.Queued or DownloadQueueItemState.Running);

    /// <summary>
    /// Aggregated progress across the whole queue: downloaded bytes vs total
    /// bytes known so far (queued items contribute 0 until their size is known).
    /// </summary>
    public QueueAggregateProgress GetAggregateProgress()
    {
        var items = Items;
        var totalBytes = items.Sum(i => i.TotalBytes);
        var downloadedBytes = items.Sum(i => i.DownloadedBytes);
        var active = items.Count(i => i.State is DownloadQueueItemState.Queued or DownloadQueueItemState.Running);
        var completed = items.Count(i => i.State == DownloadQueueItemState.Completed);
        return new QueueAggregateProgress(
            Percent: totalBytes > 0 ? downloadedBytes * 100d / totalBytes : (active > 0 ? 0 : 100),
            ActiveItems: active,
            CompletedItems: completed,
            TotalItems: items.Count,
            DownloadedBytes: downloadedBytes,
            TotalBytes: totalBytes);
    }

    /// <summary>Raised when any item changes (state or progress). Marshalled by subscribers.</summary>
    public event Action? Changed;

    /// <summary>
    /// Enqueues the recommended ASR model download. Returns the queue item.
    /// If an identical download is already queued/running, returns the existing item.
    /// When <paramref name="forceRedownload"/> is true, the existing on-disk model
    /// directory is deleted first (used to repair a broken/partial install).
    /// </summary>
    public DownloadQueueItem EnqueueAsrModel(string targetRoot, Action<string> onCompleted, bool forceRedownload = false)
    {
        if (forceRedownload)
            DeleteAsrModelDirectory(targetRoot);

        var existing = FindDuplicate(ModelKind.Asr);
        if (existing is not null)
            return existing;

        var item = new DownloadQueueItem(
            id: Guid.NewGuid(),
            kind: ModelKind.Asr,
            displayName: "ASR model (Nemotron 3.5 streaming int4)",
            enqueuedAtUtc: DateTime.UtcNow,
            onCompleted);

        Register(item);
        _ = RunItemAsync(item, ct => DownloadAsrAsync(item, targetRoot, ct));
        return item;
    }

    /// <summary>
    /// Enqueues the LiteRT-LM translation model download (~2.6 GB single file).
    /// When <paramref name="forceRedownload"/> is true, the existing model file is
    /// deleted first (used to repair a broken/partial download).
    /// </summary>
    public DownloadQueueItem EnqueueTranslationModel(Action<string> onCompleted, bool forceRedownload = false)
    {
        if (forceRedownload)
            DeleteTranslationModelFile();

        var existing = FindDuplicate(ModelKind.Translation);
        if (existing is not null)
            return existing;

        var item = new DownloadQueueItem(
            id: Guid.NewGuid(),
            kind: ModelKind.Translation,
            displayName: "Translation model (Gemma 4 E2B .litertlm)",
            enqueuedAtUtc: DateTime.UtcNow,
            onCompleted);

        Register(item);
        _ = RunItemAsync(item, ct => DownloadTranslationAsync(item, ct));
        return item;
    }

    public void Cancel(Guid id)
    {
        if (_items.TryGetValue(id, out var item))
            item.Cancel();
    }

    /// <summary>
    /// Removes an item from the queue. Running/queued items are cancelled first;
    /// finished (completed/failed/cancelled) items are simply dropped from the list.
    /// </summary>
    public void Remove(Guid id)
    {
        if (!_items.TryGetValue(id, out var item))
            return;

        item.Cancel();
        if (_items.TryRemove(id, out _))
            Changed?.Invoke();
    }

    /// <summary>Re-enqueues a failed/cancelled item as a fresh download.</summary>
    public DownloadQueueItem? Retry(Guid id)
    {
        if (!_items.TryGetValue(id, out var item))
            return null;
        if (item.State is DownloadQueueItemState.Queued or DownloadQueueItemState.Running)
            return item; // already in flight

        Remove(id);
        return item.Kind switch
        {
            ModelKind.Asr => EnqueueAsrModel(
                string.IsNullOrWhiteSpace(item.ResultPath)
                    ? AppPaths.ModelsDir
                    : Path.GetDirectoryName(item.ResultPath) ?? AppPaths.ModelsDir,
                item.OnCompleted),
            ModelKind.Translation => EnqueueTranslationModel(item.OnCompleted),
            _ => null
        };
    }

    public void CancelAll()
    {
        foreach (var item in Items)
            item.Cancel();
    }

    public void Dispose()
    {
        CancelAll();
        _http.Dispose();
        _parallelismGate.Dispose();
    }

    // ── Queue internals ────────────────────────────────────────────────────

    private DownloadQueueItem? FindDuplicate(ModelKind kind) =>
        Items.FirstOrDefault(i => i.Kind == kind
            && i.State is DownloadQueueItemState.Queued or DownloadQueueItemState.Running);

    private void Register(DownloadQueueItem item)
    {
        _items[item.Id] = item;
        item.Changed += () => Changed?.Invoke();
        Changed?.Invoke();
    }

    private async Task RunItemAsync(DownloadQueueItem item, Func<CancellationToken, Task<string>> work)
    {
        item.SetState(DownloadQueueItemState.Queued);
        await _parallelismGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (item.CancellationToken.IsCancellationRequested)
            {
                item.SetCancelled();
                return;
            }

            item.SetState(DownloadQueueItemState.Running);
            var resultPath = await work(item.CancellationToken).ConfigureAwait(false);

            if (item.CancellationToken.IsCancellationRequested)
            {
                item.SetCancelled();
                return;
            }

            item.SetCompleted(resultPath);
        }
        catch (OperationCanceledException)
        {
            item.SetCancelled();
        }
        catch (Exception ex)
        {
            item.SetFailed(ex.Message);
        }
        finally
        {
            _parallelismGate.Release();
            Changed?.Invoke();
        }
    }

    // ── Download implementations ───────────────────────────────────────────

    private async Task<string> DownloadAsrAsync(DownloadQueueItem item, string targetRoot, CancellationToken ct)
    {
        var repoId = ModelDownloadService.RecommendedRepoId;
        var subfolder = repoId[(repoId.LastIndexOf('/') + 1)..];
        var modelRoot = Path.GetFullPath(Path.Combine(targetRoot, subfolder));
        Directory.CreateDirectory(modelRoot);

        item.SetStatus($"Fetching {repoId}...");
        var files = await FetchFilesAsync(repoId, ct).ConfigureAwait(false);
        if (files.Count == 0)
            throw new InvalidOperationException("The model repository did not contain any files.");

        item.SetTotals(totalBytes: files.Sum(f => f.SizeBytes));

        for (var index = 0; index < files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[index];
            item.SetStatus($"Downloading {file.RelativePath} ({index + 1}/{files.Count})...");
            var destination = GetSafeDestination(modelRoot, file.RelativePath);
            await DownloadFileAsync(repoId, file, destination, item, ct).ConfigureAwait(false);
        }

        return modelRoot;
    }

    private async Task<string> DownloadTranslationAsync(DownloadQueueItem item, CancellationToken ct)
    {
        var destination = TranslationModelInfo.LocalModelPath;
        item.SetStatus($"Fetching {TranslationModelInfo.RepoId}...");

        var files = await FetchFilesAsync(TranslationModelInfo.RepoId, ct).ConfigureAwait(false);
        var modelFile = files.FirstOrDefault(f =>
            string.Equals(f.RelativePath, TranslationModelInfo.FileName, StringComparison.Ordinal));
        if (modelFile.RelativePath.Length == 0)
            throw new InvalidOperationException(
                $"{TranslationModelInfo.FileName} was not found in {TranslationModelInfo.RepoId}.");

        item.SetTotals(totalBytes: modelFile.SizeBytes);
        item.SetStatus($"Downloading {TranslationModelInfo.FileName}...");

        await DownloadFileAsync(TranslationModelInfo.RepoId, modelFile, destination, item, ct)
            .ConfigureAwait(false);

        return destination;
    }

    private async Task<List<RemoteFile>> FetchFilesAsync(string repoId, CancellationToken ct)
    {
        // blobs=true is required — without it the API omits file sizes, which
        // leaves the queue progress at "0/N done" with no byte-level percent.
        var endpoint = $"https://huggingface.co/api/models/{repoId}?blobs=true";
        using var response = await _http.GetAsync(endpoint, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("siblings", out var siblings))
            return [];

        return siblings.EnumerateArray()
            .Select(file => new RemoteFile(
                file.GetProperty("rfilename").GetString() ?? string.Empty,
                file.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number
                    ? size.GetInt64()
                    : 0))
            .Where(file => !string.IsNullOrWhiteSpace(file.RelativePath)
                && !file.RelativePath.StartsWith(".", StringComparison.Ordinal))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<long> DownloadFileAsync(
        string repoId,
        RemoteFile file,
        string destination,
        DownloadQueueItem item,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaryPath = destination + ".part";
        var endpoint = $"https://huggingface.co/{repoId}/resolve/main/{Uri.EscapeDataString(file.RelativePath).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)}";
        using var response = await _http.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long fileBytes;
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[128 * 1024];
            fileBytes = 0;
            var stopwatch = Stopwatch.StartNew();
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                fileBytes += read;
                if (stopwatch.ElapsedMilliseconds >= 100)
                {
                    item.AddDownloaded(read);
                    stopwatch.Restart();
                }
            }

            if (fileBytes > 0)
                item.AddDownloaded(0); // flush pending bytes

            await output.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(temporaryPath, destination, overwrite: true);
        return fileBytes;
    }

    private static string GetSafeDestination(string modelRoot, string relativePath)
    {
        var destination = Path.GetFullPath(Path.Combine(modelRoot, relativePath));
        var rootWithSeparator = modelRoot.EndsWith(Path.DirectorySeparatorChar)
            ? modelRoot
            : modelRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsafe model file path: {relativePath}");

        return destination;
    }

    /// <summary>
    /// Deletes the on-disk ASR model directory (and any stale .part files) so a
    /// broken/partial install can be re-downloaded cleanly. Best effort — a busy
    /// or missing directory is not an error.
    /// </summary>
    private static void DeleteAsrModelDirectory(string targetRoot)
    {
        try
        {
            var repoId = ModelDownloadService.RecommendedRepoId;
            var subfolder = repoId[(repoId.LastIndexOf('/') + 1)..];
            var modelRoot = Path.GetFullPath(Path.Combine(targetRoot, subfolder));
            if (Directory.Exists(modelRoot))
                Directory.Delete(modelRoot, recursive: true);

            foreach (var part in Directory.EnumerateFiles(targetRoot, "*.part", SearchOption.AllDirectories))
            {
                try { File.Delete(part); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VoiceType.Uno] Could not delete ASR model directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the on-disk translation model file (and its .part) so a
    /// broken/partial download can be re-downloaded cleanly. Best effort.
    /// </summary>
    private static void DeleteTranslationModelFile()
    {
        try
        {
            var path = TranslationModelInfo.LocalModelPath;
            if (File.Exists(path))
                File.Delete(path);

            var part = path + ".part";
            if (File.Exists(part))
                File.Delete(part);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[VoiceType.Uno] Could not delete translation model file: {ex.Message}");
        }
    }

    private readonly record struct RemoteFile(string RelativePath, long SizeBytes);
}

public enum ModelKind { Asr, Translation }

public enum DownloadQueueItemState { Queued, Running, Completed, Failed, Cancelled }

/// <summary>
/// A single model download in the queue. Thread-safe progress reporting;
/// subscribers marshal to the UI thread.
/// </summary>
public sealed class DownloadQueueItem
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _downloadedBytes;
    private long _pendingDelta;

    public DownloadQueueItem(Guid id, ModelKind kind, string displayName, DateTime enqueuedAtUtc, Action<string> onCompleted)
    {
        Id = id;
        Kind = kind;
        DisplayName = displayName;
        EnqueuedAtUtc = enqueuedAtUtc;
        OnCompleted = onCompleted;
    }

    public Guid Id { get; }
    public ModelKind Kind { get; }
    public string DisplayName { get; }
    public DateTime EnqueuedAtUtc { get; }
    public Action<string> OnCompleted { get; }
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>Awaitable completion: resolves with the result path, faults on failure, cancels on Cancel().</summary>
    public Task<string> Completion => _completion.Task;

    public DownloadQueueItemState State { get; private set; } = DownloadQueueItemState.Queued;
    public string Status { get; private set; } = "Queued";
    public string? ErrorMessage { get; private set; }
    public string? ResultPath { get; private set; }
    public long TotalBytes { get; private set; }
    public long DownloadedBytes => Interlocked.Read(ref _downloadedBytes);

    public double Percent => TotalBytes > 0
        ? DownloadedBytes * 100d / TotalBytes
        : State == DownloadQueueItemState.Completed ? 100 : 0;

    public event Action? Changed;

    public void Cancel() => _cts.Cancel();

    public void SetState(DownloadQueueItemState state)
    {
        State = state;
        Changed?.Invoke();
    }

    public void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void SetTotals(long totalBytes)
    {
        TotalBytes = totalBytes;
        Changed?.Invoke();
    }

    /// <summary>
    /// Accumulates downloaded bytes. Coalesced: deltas are batched and flushed
    /// at most once per call interval (callers throttle to ~100 ms).
    /// </summary>
    public void AddDownloaded(long deltaBytes)
    {
        if (deltaBytes > 0)
            Interlocked.Add(ref _pendingDelta, deltaBytes);

        var pending = Interlocked.Exchange(ref _pendingDelta, 0);
        if (pending > 0)
        {
            Interlocked.Add(ref _downloadedBytes, pending);
            Changed?.Invoke();
        }
    }

    public void SetCompleted(string resultPath)
    {
        State = DownloadQueueItemState.Completed;
        Status = "Completed";
        ResultPath = resultPath;
        OnCompleted(resultPath);
        _completion.TrySetResult(resultPath);
        Changed?.Invoke();
    }

    public void SetFailed(string message)
    {
        State = DownloadQueueItemState.Failed;
        Status = "Failed";
        ErrorMessage = message;
        _completion.TrySetException(new InvalidOperationException(message));
        Changed?.Invoke();
    }

    public void SetCancelled()
    {
        State = DownloadQueueItemState.Cancelled;
        Status = "Cancelled";
        _completion.TrySetCanceled();
        Changed?.Invoke();
    }
}

/// <summary>Aggregated progress across the whole download queue.</summary>
public readonly record struct QueueAggregateProgress(
    double Percent,
    int ActiveItems,
    int CompletedItems,
    int TotalItems,
    long DownloadedBytes,
    long TotalBytes);
