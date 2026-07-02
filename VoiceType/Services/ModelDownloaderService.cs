using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace VoiceType.Services;

/// <summary>
/// Downloads ONNX model files from HuggingFace or custom URLs with progress tracking,
/// resume support, and resilience (retry + exponential backoff).
/// </summary>
public sealed class ModelDownloaderService : IDisposable
{
    private const int ProgressBufferSize = 64 * 1024;
    private const int ProgressUpdateIntervalMs = 125;
    private const double ProgressStepPercent = 1.0;

    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;

    /// <summary>Raised periodically during download with structured progress info.</summary>
    public event Action<DownloadProgress>? ProgressChanged;
    /// <summary>Raised for human-readable status messages.</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Raised when download completes (ok=true) or fails/cancelled (ok=false).</summary>
    public event Action<bool, string>? Completed;

    public bool IsDownloading { get; private set; }

    public ModelDownloaderService()
    {
        // ── Inner handler: pooled connections with DNS refresh ──
        var socketHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };

        // ── Resilience: retry on transient HTTP failures ──
        var retryPipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                BackoffType = DelayBackoffType.Exponential,
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                UseJitter = true
            })
            .Build();

        _http = new HttpClient(new ResilienceHandler(retryPipeline) { InnerHandler = socketHandler })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "User-Agent", "VoiceType/1.0" } }
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════

    /// <summary>Fetch files from repo and group by top-level folders.</summary>
    public async Task<List<HfFolder>> FetchRepoFolders(string repoId, CancellationToken ct = default)
    {
        StatusChanged?.Invoke($"Fetching file list from {repoId}...");
        var url = $"https://huggingface.co/api/models/{repoId}";
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var allFiles = new List<HfFile>();

        if (json.TryGetProperty("siblings", out var siblings))
        {
            foreach (var sib in siblings.EnumerateArray())
            {
                var rfilename = sib.GetProperty("rfilename").GetString() ?? "";
                if (rfilename.StartsWith(".")) continue;
                var size = sib.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                allFiles.Add(new HfFile { Name = Path.GetFileName(rfilename), RelativePath = rfilename, SizeBytes = size });
            }
        }

        // Group by top-level folder
        var folders = allFiles
            .GroupBy(f => f.RelativePath.Contains('/') ? f.RelativePath[..f.RelativePath.IndexOf('/')] : "(root)")
            .OrderBy(g => g.Key == "(root)" ? 0 : 1).ThenBy(g => g.Key)
            .Select(g => new HfFolder
            {
                Name = g.Key == "(root)" ? "📄 Root files" : $"📁 {g.Key}",
                Files = g.OrderBy(f => f.RelativePath).ToList()
            })
            .ToList();

        StatusChanged?.Invoke($"Found {folders.Count} folder(s), {allFiles.Count} file(s) in {repoId}");
        return folders;
    }

    /// <summary>
    /// Download selected folders from a HuggingFace repo into targetRoot/{folderName}/.
    /// Each file gets a per-file timeout of 10 minutes.
    /// </summary>
    public async Task DownloadFromHuggingFace(string repoId, List<HfFolder> folders, string targetRoot)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Directory.CreateDirectory(targetRoot);

        var selectedFolders = folders.Where(f => f.Selected).ToList();
        var files = new List<FileToDownload>();

        foreach (var folder in selectedFolders)
        {
            foreach (var file in folder.Files)
            {
                var url = $"https://huggingface.co/{repoId}/resolve/main/{file.RelativePath}";
                var subfolder = string.IsNullOrEmpty(folder.SubfolderName)
                    ? repoId[(repoId.LastIndexOf('/') + 1)..]
                    : folder.SubfolderName;
                var relativePath = file.RelativePath.Contains('/')
                    ? file.RelativePath[(file.RelativePath.IndexOf('/') + 1)..]
                    : file.RelativePath;
                var dest = Path.Combine(targetRoot, subfolder, relativePath);
                files.Add(new FileToDownload(url, dest, file.SizeBytes));
            }
        }

        StatusChanged?.Invoke($"Starting download: {files.Count} file(s), {FormatSize(files.Sum(f => f.SizeBytes))}");
        await DownloadBatchAsync(files, _cts.Token);
    }

    /// <summary>Download all files from a HuggingFace repo into targetRoot/{subfolder}/.</summary>
    public async Task DownloadModelRepo(string repoId, string subfolder, string targetRoot,
        CancellationToken externalCt = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        IsDownloading = true;
        Directory.CreateDirectory(targetRoot);

        StatusChanged?.Invoke($"Fetching {repoId}...");
        var url = $"https://huggingface.co/api/models/{repoId}";
        var response = await _http.GetAsync(url, _cts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(_cts.Token);

        var files = new List<FileToDownload>();
        if (json.TryGetProperty("siblings", out var siblings))
        {
            foreach (var sib in siblings.EnumerateArray())
            {
                var rfilename = sib.GetProperty("rfilename").GetString() ?? "";
                if (rfilename.StartsWith(".")) continue;
                var size = sib.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                var dest = Path.Combine(targetRoot, subfolder, rfilename);
                var fileUrl = $"https://huggingface.co/{repoId}/resolve/main/{rfilename}";
                files.Add(new FileToDownload(fileUrl, dest, size));
            }
        }

        StatusChanged?.Invoke($"Starting download: {files.Count} file(s), {FormatSize(files.Sum(f => f.SizeBytes))}");
        await DownloadBatchAsync(files, _cts.Token);
    }

    /// <summary>Download a single file from a direct URL.</summary>
    public async Task DownloadSingleFile(string url, string destPath)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        var dir = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(dir);
        var fileName = Path.GetFileName(destPath);

        try
        {
            StatusChanged?.Invoke($"Downloading {fileName}...");
            await DownloadFileAsync(url, destPath, 0, _cts.Token);
            StatusChanged?.Invoke($"Download complete → {destPath}");
            Completed?.Invoke(true, destPath);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Download cancelled");
            Completed?.Invoke(false, "Cancelled");
            throw;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error on {fileName}: {ex.Message}");
            Completed?.Invoke(false, ex.Message);
            throw;
        }
        finally
        {
            IsDownloading = false;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        IsDownloading = false;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _http.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    //  Private download pipeline (shared by all batch methods)
    // ═══════════════════════════════════════════════════════════

    private readonly record struct FileToDownload(string Url, string DestPath, long SizeBytes);

    /// <summary>Core batch download — iterates files, reports progress, handles errors.</summary>
    private async Task DownloadBatchAsync(IReadOnlyList<FileToDownload> files, CancellationToken ct)
    {
        long totalBytes = files.Sum(f => f.SizeBytes);
        long downloadedBytes = 0;
        int completed = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(file.DestPath);
            StatusChanged?.Invoke($"Downloading {fileName} ({FormatSize(file.SizeBytes)})...");

            // Per-file timeout: 10 minutes
            using var fileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fileCts.CancelAfter(TimeSpan.FromMinutes(10));

            long fileChunkTotal = 0;
            var progressGate = Stopwatch.StartNew();
            double lastFileProgress = -1;
            double lastOverallProgress = -1;

            try
            {
                await DownloadFileWithProgressAsync(file.Url, file.DestPath, file.SizeBytes,
                    chunkBytes =>
                    {
                        downloadedBytes += chunkBytes;
                        fileChunkTotal += chunkBytes;
                        EmitProgressIfNeeded(force: false);
                    },
                    fileCts.Token);
                completed++;
                EmitProgressIfNeeded(force: true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-file timeout — treat as batch failure
                StatusChanged?.Invoke($"Timeout on {fileName} (10 min), aborting batch");
                IsDownloading = false;
                Completed?.Invoke(false, $"Timeout downloading {fileName}");
                return;
            }
            catch (OperationCanceledException)
            {
                StatusChanged?.Invoke("Download cancelled");
                IsDownloading = false;
                Completed?.Invoke(false, "Cancelled");
                return;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Error on {fileName}: {ex.Message}");
                IsDownloading = false;
                Completed?.Invoke(false, ex.Message);
                return;
            }

            void EmitProgressIfNeeded(bool force)
            {
                var fileProgress = file.SizeBytes > 0
                    ? (double)fileChunkTotal / file.SizeBytes * 100
                    : 100;
                var overallProgress = totalBytes > 0
                    ? (double)downloadedBytes / totalBytes * 100
                    : 100;

                if (!force)
                {
                    var progressChangedEnough = Math.Abs(fileProgress - lastFileProgress) >= ProgressStepPercent
                        || Math.Abs(overallProgress - lastOverallProgress) >= ProgressStepPercent;
                    if (!progressChangedEnough && progressGate.ElapsedMilliseconds < ProgressUpdateIntervalMs)
                        return;
                }

                lastFileProgress = fileProgress;
                lastOverallProgress = overallProgress;
                progressGate.Restart();

                ProgressChanged?.Invoke(new DownloadProgress
                {
                    CurrentFile = fileName,
                    FileProgress = fileProgress,
                    OverallProgress = overallProgress,
                    DownloadedFiles = completed,
                    TotalFiles = files.Count
                });
            }
        }

        IsDownloading = false;
        StatusChanged?.Invoke($"Download complete — {completed} file(s)");
        Completed?.Invoke(true, "");
    }

    // ═══════════════════════════════════════════════════════════
    //  Low-level file download with resume support
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Download a single file to dest with resume support and per-chunk progress callback.
    /// </summary>
    private async Task DownloadFileWithProgressAsync(string url, string dest, long totalSize,
        Action<long> onChunk, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        long existingBytes = 0;
        if (File.Exists(dest))
        {
            existingBytes = new FileInfo(dest).Length;
            if (totalSize > 0 && existingBytes >= totalSize)
            {
                onChunk(existingBytes);
                return; // already complete
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        if (existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            onChunk(existingBytes);
            await using var file = File.Open(dest, FileMode.Append, FileAccess.Write);
            await CopyStreamAsync(stream, file, onChunk, ct);
        }
        else
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(dest);
            await CopyStreamAsync(stream, file, onChunk, ct);
        }
    }

    /// <summary>Simpler overload — no per-chunk callback (used by DownloadSingleFile).</summary>
    private async Task DownloadFileAsync(string url, string dest, long totalSize, CancellationToken ct)
    {
        await DownloadFileWithProgressAsync(url, dest, totalSize, _ => { }, ct);
    }

    /// <summary>Copy stream to stream in chunks, invoking callback with bytes written.</summary>
    private static async Task CopyStreamAsync(Stream source, Stream target,
        Action<long> onChunk, CancellationToken ct)
    {
        var buffer = new byte[ProgressBufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            onChunk(read);
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Utility
    // ═══════════════════════════════════════════════════════════

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:F1} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        >= 1_000 => $"{bytes / 1_000.0:F1} KB",
        _ => $"{bytes} B"
    };
}

// ═══════════════════════════════════════════════════════════════
//  Models
// ═══════════════════════════════════════════════════════════════

public sealed class HfFile
{
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public long DownloadedBytes { get; set; }
}

/// <summary>A folder containing HuggingFace files (grouped by top-level directory).</summary>
public sealed class HfFolder
{
    public string Name { get; init; } = "";
    public string SubfolderName => Name switch
    {
        _ when Name.StartsWith("📁 ") => Name[3..],
        "📄 Root files" => "",
        _ when Name.StartsWith("📄 ") => Name[3..],
        "(root)" => "",
        _ => Name
    };
    public List<HfFile> Files { get; init; } = new();
    public long TotalSize => Files.Sum(f => f.SizeBytes);
    public string SizeDisplay => ModelDownloaderService.FormatSize(TotalSize);
    public bool Selected { get; set; } = true;
}

public sealed class DownloadProgress
{
    public string CurrentFile { get; init; } = "";
    public double FileProgress { get; init; }
    public double OverallProgress { get; init; }
    public int DownloadedFiles { get; init; }
    public int TotalFiles { get; init; }
}
