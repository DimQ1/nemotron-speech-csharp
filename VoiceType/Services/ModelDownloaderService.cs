using System.IO;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Diagnostics;

namespace VoiceType.Services;

/// <summary>
/// Downloads ONNX model files from HuggingFace using the Downloader library
/// (multipart parallel download, auto-resume, real-time progress).
/// </summary>
public sealed class ModelDownloaderService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _disposeHttp;
    private readonly string _huggingFaceBaseUrl;
    private CancellationTokenSource? _cts;

    /// <summary>Raised periodically during download with structured progress info.</summary>
    public event Action<DownloadProgress>? ProgressChanged;
    /// <summary>Raised for human-readable status messages.</summary>
    public event Action<string>? StatusChanged;
    /// <summary>Raised when download completes (ok=true) or fails/cancelled (ok=false).</summary>
    public event Action<bool, string>? Completed;

    public bool IsDownloading { get; private set; }

    public ModelDownloaderService()
        : this(CreateDefaultHttpClient(), "https://huggingface.co", disposeHttp: true)
    {
    }

    public ModelDownloaderService(HttpClient httpClient, string huggingFaceBaseUrl)
        : this(httpClient, huggingFaceBaseUrl, disposeHttp: false)
    {
    }

    private ModelDownloaderService(HttpClient httpClient, string huggingFaceBaseUrl, bool disposeHttp)
    {
        _http = httpClient;
        _disposeHttp = disposeHttp;
        _huggingFaceBaseUrl = huggingFaceBaseUrl.TrimEnd('/');
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            MaxConnectionsPerServer = 10,
            UseCookies = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");

        return client;
    }

    // ═══════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════

    /// <summary>Fetch files from repo and group by top-level folders.</summary>
    public async Task<List<HfFolder>> FetchRepoFolders(string repoId, CancellationToken ct = default)
    {
        StatusChanged?.Invoke($"Fetching file list from {repoId}...");
        var url = $"{_huggingFaceBaseUrl}/api/models/{repoId}";
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

    /// <summary>Download selected folders using Downloader library.</summary>
    public async Task DownloadFromHuggingFace(string repoId, List<HfFolder> folders, string targetRoot)
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Directory.CreateDirectory(targetRoot);

        var selectedFolders = folders.Where(f => f.Selected).ToList();
        var files = new List<FileToDownload>();

        foreach (var folder in selectedFolders)
        {
            var subfolder = string.IsNullOrEmpty(folder.SubfolderName)
                ? repoId[(repoId.LastIndexOf('/') + 1)..]
                : folder.SubfolderName;

            foreach (var file in folder.Files)
            {
                var url = $"{_huggingFaceBaseUrl}/{repoId}/resolve/main/{file.RelativePath}";
                var relativePath = file.RelativePath.Contains('/')
                    ? file.RelativePath[(file.RelativePath.IndexOf('/') + 1)..]
                    : file.RelativePath;
                var dest = Path.Combine(targetRoot, subfolder, relativePath);
                var displayPath = Path.Combine(subfolder, relativePath).Replace('\\', '/');
                files.Add(new FileToDownload(url, dest, displayPath, file.SizeBytes));
            }
        }

        if (files.Count == 0)
        {
            IsDownloading = false;
            StatusChanged?.Invoke("Select at least one folder to download");
            Completed?.Invoke(false, "No folders selected");
            return;
        }

        StatusChanged?.Invoke($"Starting download: {files.Count} file(s), {FormatSize(files.Sum(f => f.SizeBytes))}");
        await DownloadBatchAsync(files, _cts.Token);
    }

    /// <summary>Download all files from a HuggingFace repo using Downloader library.</summary>
    public async Task DownloadModelRepo(string repoId, string subfolder, string targetRoot,
        CancellationToken externalCt = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        IsDownloading = true;
        Directory.CreateDirectory(targetRoot);

        StatusChanged?.Invoke($"Fetching {repoId}...");
        var url = $"{_huggingFaceBaseUrl}/api/models/{repoId}";
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
                var fileUrl = $"{_huggingFaceBaseUrl}/{repoId}/resolve/main/{rfilename}";
                files.Add(new FileToDownload(fileUrl, dest, Path.Combine(subfolder, rfilename).Replace('\\', '/'), size));
            }
        }

        StatusChanged?.Invoke($"Starting download: {files.Count} file(s), {FormatSize(files.Sum(f => f.SizeBytes))}");
        await DownloadBatchAsync(files, _cts.Token);
    }

    /// <summary>Download a single file from a direct URL using Downloader library.</summary>
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
            await DownloadWithDownloaderAsync(url, destPath, 0, _cts.Token);
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
        if (_disposeHttp)
            _http.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    //  Downloader-powered file downloads
    // ═══════════════════════════════════════════════════════════

    private readonly record struct FileToDownload(string Url, string DestPath, string DisplayPath, long SizeBytes);

    private async Task DownloadBatchAsync(IReadOnlyList<FileToDownload> files, CancellationToken ct)
    {
        int completed = 0;

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            ct.ThrowIfCancellationRequested();

            StatusChanged?.Invoke($"Downloading {file.DisplayPath} ({FormatSize(file.SizeBytes)})...");

            long fileChunkTotal = 0;
            long fileActualTotal = 0;
            double lastFileProgress = -1;
            double lastOverallProgress = -1;
            var progressGate = Stopwatch.StartNew();

            try
            {
                await DownloadWithDownloaderAsync(file.Url, file.DestPath, file.SizeBytes,
                    (_, totalRead, actualTotal) =>
                    {
                        fileChunkTotal = totalRead;
                        fileActualTotal = actualTotal;
                        EmitProgressIfNeeded(force: false);
                    },
                    ct);

                completed++;
                EmitProgressIfNeeded(force: true);
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
                StatusChanged?.Invoke($"Error on {file.DisplayPath}: {ex.Message}");
                IsDownloading = false;
                Completed?.Invoke(false, ex.Message);
                return;
            }

            void EmitProgressIfNeeded(bool force)
            {
                // Use actual Content-Length from HTTP response (authoritative),
                // fall back to file.SizeBytes from API
                var effectiveSize = fileActualTotal > 0 ? fileActualTotal : file.SizeBytes;
                var fileProgress = effectiveSize > 0
                    ? (double)fileChunkTotal / effectiveSize * 100
                    : 100;
                // Overall progress based on completed files count only
                var overallProgress = (double)completed / files.Count * 100;

                if (!force)
                {
                    var changed = Math.Abs(fileProgress - lastFileProgress) >= 0.5
                        || Math.Abs(overallProgress - lastOverallProgress) >= 0.5;
                    if (!changed && progressGate.ElapsedMilliseconds < 125)
                        return;
                }

                lastFileProgress = fileProgress;
                lastOverallProgress = overallProgress;
                progressGate.Restart();

                ProgressChanged?.Invoke(new DownloadProgress
                {
                    CurrentFile = file.DisplayPath,
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

    /// <summary>Download a single file using plain HttpClient streaming.
    /// Handles HuggingFace's 307 → CloudFront CDN redirect correctly because
    /// _http already has AllowAutoRedirect=true + browser User-Agent.</summary>
    private async Task DownloadWithDownloaderAsync(string url, string dest, long knownSize,
        Action<long, long, long>? onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        ct.ThrowIfCancellationRequested();

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Use Content-Length from the response (after redirect) as authoritative file size
        var actualSize = response.Content.Headers.ContentLength > 0
            ? response.Content.Headers.ContentLength.Value
            : knownSize;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            onProgress?.Invoke(bytesRead, totalRead, actualSize);
        }
    }

    /// <summary>Simpler overload — no per-chunk callback.</summary>
    private async Task DownloadWithDownloaderAsync(string url, string dest, long knownSize, CancellationToken ct)
    {
        await DownloadWithDownloaderAsync(url, dest, knownSize, (Action<long, long, long>?)null, ct);
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
public sealed class HfFolder : INotifyPropertyChanged
{
    private bool _selected = true;

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
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;

            _selected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DownloadProgress
{
    public string CurrentFile { get; init; } = "";
    public double FileProgress { get; init; }
    public double OverallProgress { get; init; }
    public int DownloadedFiles { get; init; }
    public int TotalFiles { get; init; }
}
